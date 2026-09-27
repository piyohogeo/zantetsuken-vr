using System;
using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>The outcome of one artificial movement request.</summary>
    public enum LocomotionVerdict
    {
        /// <summary>Neither capsule overlaps: the candidate pose was applied.</summary>
        Allowed,

        /// <summary>The Root capsule overlaps: nothing was applied.</summary>
        RejectedRoot,

        /// <summary>The HMD capsule overlaps: nothing was applied.</summary>
        RejectedHmd,

        /// <summary>Both overlap: nothing was applied.</summary>
        RejectedBoth,
    }

    /// <summary>One authored capsule: its pose in its parent's space (the Root, or the HMD) and its size.</summary>
    [Serializable]
    public struct PlayerCapsuleAuthoring
    {
        [Tooltip("The capsule's centre in the parent's local space, in metres.")]
        public Vector3 localCenter;

        [Tooltip("The capsule's rotation in the parent's local space, in degrees. Its length runs along the resulting Y axis.")]
        public Vector3 localEulerAngles;

        [Tooltip("The radius, in metres.")]
        public float radius;

        [Tooltip("The height end to end, in metres.")]
        public float height;

        public PlayerCapsuleAuthoring(Vector3 localCenter, Vector3 localEulerAngles, float radius, float height)
        {
            this.localCenter = localCenter;
            this.localEulerAngles = localEulerAngles;
            this.radius = radius;
            this.height = height;
        }

        /// <summary>The capsule placed under <paramref name="parent"/>, in world space.</summary>
        public LocomotionCapsule Place(in Pose parent)
        {
            return LocomotionCapsule.Upright(
                parent.position + (parent.rotation * localCenter),
                parent.rotation * Quaternion.Euler(localEulerAngles),
                radius,
                height);
        }
    }

    /// <summary>
    /// The player's two locomotion capsules (DESIGN 7.2.3): one on the Root, one on the HMD. Settled at initialisation
    /// and never changed afterwards.
    /// </summary>
    public readonly struct PlayerLocomotionBody
    {
        public readonly PlayerCapsuleAuthoring Root;
        public readonly PlayerCapsuleAuthoring Hmd;

        public PlayerLocomotionBody(PlayerCapsuleAuthoring root, PlayerCapsuleAuthoring hmd)
        {
            Root = root;
            Hmd = hmd;
        }
    }

    /// <summary>
    /// Artificial movement of the player against the level's fixed occupancy (DESIGN 7.2.3 / D-131 / D-166).
    /// <para>
    /// A request names the candidate Root pose. Two capsules are placed for it: the Root capsule on the candidate
    /// Root, and the HMD capsule on the current tracked HMD pose carried onto the candidate Root (the HMD kept where it
    /// is relative to the Root). If either overlaps the occupancy the whole request -- position and rotation -- is
    /// rejected and nothing moves; otherwise the Root takes the candidate pose. There is no sliding along a wall, no
    /// partial move, no search for a way out and no sweep, so a long step can pass through a thin wall.
    /// </para>
    /// <para>
    /// Only artificial movement is judged. The tracked HMD and controllers are never written or held back: leaning into
    /// a wall in real space is not prevented and does not move the Root, and a request made while leaning in is judged
    /// like any other. Where the player is placed at spawn or reset is the caller's responsibility; nothing here pushes
    /// the player out of the occupancy.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerLocomotion : MonoBehaviour
    {
        [Tooltip("The level whose fixed occupancy requests are judged against.")]
        [SerializeField] private PlayerLocomotionLevel level;

        [Tooltip("The tracked HMD (the XR Origin's Main Camera). Only read, never written.")]
        [SerializeField] private Transform hmd;

        [Tooltip("The Root capsule, in the Root's (this object's) local space.")]
        [SerializeField] private PlayerCapsuleAuthoring rootCapsule =
            new PlayerCapsuleAuthoring(new Vector3(0f, 0.9f, 0f), Vector3.zero, 0.25f, 1.6f);

        [Tooltip("The HMD capsule, in the HMD's local space.")]
        [SerializeField] private PlayerCapsuleAuthoring hmdCapsule =
            new PlayerCapsuleAuthoring(new Vector3(0f, -0.05f, -0.05f), Vector3.zero, 0.15f, 0.4f);

        private MobPlan.WalkableMap _map;
        private Vector3 _mapOffset;
        private float _circleRadius = 1.5f;
        public void ConfigureMap(MobPlan.WalkableMap map, Vector3 offset, float radius = 1.5f)
        { _map = map; _mapOffset = offset; _circleRadius = radius; }

        private PlayerLocomotionBody _body;
        private bool _initialised;

        /// <summary>The capsules settled at initialisation.</summary>
        public PlayerLocomotionBody Body
        {
            get
            {
                EnsureInitialised();
                return _body;
            }
        }

        /// <summary>The occupancy requests are judged against: the level's, or an empty one without a level.</summary>
        public PlayerLocomotionOccupancy Occupancy => level != null ? level.Occupancy : PlayerLocomotionOccupancy.Empty;

        /// <summary>The outcome of the latest request.</summary>
        public LocomotionVerdict LastVerdict { get; private set; }

        public int AllowedCount { get; private set; }

        public int RejectedCount { get; private set; }

        /// <summary>The Root's current world pose.</summary>
        public Pose RootPose => new Pose(transform.position, transform.rotation);

        /// <summary>The current tracked HMD pose relative to the Root; the Root's own pose without an HMD.</summary>
        public Pose CurrentHmdInRoot
        {
            get
            {
                if (hmd == null)
                {
                    return new Pose(Vector3.zero, Quaternion.identity);
                }

                Quaternion inverse = Quaternion.Inverse(transform.rotation);
                return new Pose(inverse * (hmd.position - transform.position), inverse * hmd.rotation);
            }
        }

        /// <summary>Sets the authoring before initialisation (for building scenes and tests from code).</summary>
        public void Configure(PlayerLocomotionLevel occupancyLevel, Transform trackedHmd, PlayerCapsuleAuthoring root, PlayerCapsuleAuthoring head)
        {
            level = occupancyLevel;
            hmd = trackedHmd;
            rootCapsule = root;
            hmdCapsule = head;
            _initialised = false;
        }

        /// <summary>
        /// The live entry: judges <paramref name="candidateRoot"/> with the HMD where it is tracked now, and applies it
        /// if allowed.
        /// </summary>
        public LocomotionVerdict TryRequest(Pose candidateRoot)
        {
            return TryRequest(candidateRoot, CurrentHmdInRoot);
        }

        /// <summary>
        /// The same entry with the HMD pose relative to the Root given, for a fixed replay of a tracked pose. The tracked
        /// HMD itself is not touched.
        /// </summary>
        public LocomotionVerdict TryRequest(Pose candidateRoot, Pose hmdInRoot)
        {
            if (_map != null)
            {
                Vector3 from = transform.position - _mapOffset;
                Vector3 to = candidateRoot.position - _mapOffset;
                var position = MobPlan.CircleLocomotion.Move(_map, _circleRadius, from.x, from.z, to.x, to.z);
                bool moved = position.X != from.x || position.Z != from.z;
                transform.SetPositionAndRotation(new Vector3(position.X + _mapOffset.x, transform.position.y, position.Z + _mapOffset.z), candidateRoot.rotation);
                LastVerdict = moved || (to.x == from.x && to.z == from.z) ? LocomotionVerdict.Allowed : LocomotionVerdict.RejectedRoot;
                if (LastVerdict == LocomotionVerdict.Allowed) AllowedCount++; else RejectedCount++;
                return LastVerdict;
            }
            EnsureInitialised();
            LocomotionVerdict verdict = Judge(Occupancy, _body, candidateRoot, hmdInRoot);
            LastVerdict = verdict;
            if (verdict == LocomotionVerdict.Allowed)
            {
                AllowedCount++;
                transform.SetPositionAndRotation(candidateRoot.position, candidateRoot.rotation);
            }
            else
            {
                RejectedCount++;
            }

            return verdict;
        }

        /// <summary>
        /// The judgement alone: whether the Root capsule on <paramref name="candidateRoot"/> and the HMD capsule on
        /// <paramref name="hmdInRoot"/> carried onto it overlap <paramref name="occupancy"/>.
        /// </summary>
        public static LocomotionVerdict Judge(
            PlayerLocomotionOccupancy occupancy, in PlayerLocomotionBody body, Pose candidateRoot, Pose hmdInRoot)
        {
            Pose candidateHmd = new Pose(
                candidateRoot.position + (candidateRoot.rotation * hmdInRoot.position),
                candidateRoot.rotation * hmdInRoot.rotation);
            bool rootHits = occupancy.Overlaps(body.Root.Place(candidateRoot));
            bool hmdHits = occupancy.Overlaps(body.Hmd.Place(candidateHmd));
            if (rootHits && hmdHits)
            {
                return LocomotionVerdict.RejectedBoth;
            }

            if (rootHits)
            {
                return LocomotionVerdict.RejectedRoot;
            }

            return hmdHits ? LocomotionVerdict.RejectedHmd : LocomotionVerdict.Allowed;
        }

        /// <summary>
        /// A candidate Root pose for a turn and a move: the Root turned by <paramref name="yawDegrees"/> about the
        /// vertical line through the HMD, so the head stays where it is while the view turns, then moved by
        /// <paramref name="worldMove"/>.
        /// </summary>
        public static Pose Candidate(Pose root, Vector3 hmdWorldPosition, Vector3 worldMove, float yawDegrees)
        {
            Quaternion turn = Quaternion.AngleAxis(yawDegrees, Vector3.up);
            Vector3 pivot = new Vector3(hmdWorldPosition.x, root.position.y, hmdWorldPosition.z);
            Vector3 position = pivot + (turn * (root.position - pivot)) + worldMove;
            return new Pose(position, turn * root.rotation);
        }

        private void Awake()
        {
            EnsureInitialised();
        }

        private void EnsureInitialised()
        {
            if (_initialised)
            {
                return;
            }

            _body = new PlayerLocomotionBody(rootCapsule, hmdCapsule);
            _initialised = true;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            LocomotionCapsule root = rootCapsule.Place(RootPose);
            Gizmos.DrawWireSphere(root.a, root.radius);
            Gizmos.DrawWireSphere(root.b, root.radius);
            if (hmd != null)
            {
                LocomotionCapsule head = hmdCapsule.Place(new Pose(hmd.position, hmd.rotation));
                Gizmos.DrawWireSphere(head.a, head.radius);
                Gizmos.DrawWireSphere(head.b, head.radius);
            }
        }
    }
}
