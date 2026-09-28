using System;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The building metadata of one physics owner (DESIGN 7.2.2): whether it comes from a building, and how many 1→2
    /// physics splits its building lineage has published. It is the owner's own, carried by value; nothing else keeps
    /// it and nothing infers it from names, bounds or materials.
    /// <para>
    /// An owner registered as a building starts at depth 0. Every other owner is not building-derived and stays at 0
    /// whatever is cut of it. A child's depth is planned from its parent's before anything of the cut is built
    /// (<see cref="ChildOfSplit"/>), and that one value is what the Provisional pair and the published children carry.
    /// </para>
    /// </summary>
    public readonly struct BuildingLineage : IEquatable<BuildingLineage>
    {
        private BuildingLineage(bool isBuildingDerived, int splitDepth)
        {
            IsBuildingDerived = isBuildingDerived;
            SplitDepth = splitDepth;
        }

        /// <summary>Not from a building: false, depth 0.</summary>
        public static BuildingLineage NotBuilding => default;

        /// <summary>A building as it is registered, before anything of it has been split: true, depth 0.</summary>
        public static BuildingLineage RegisteredBuilding => new BuildingLineage(true, 0);

        public bool IsBuildingDerived { get; }

        /// <summary>How many 1→2 physics splits of the building lineage were published down to this owner.</summary>
        public int SplitDepth { get; }

        /// <summary>
        /// What the two children of one split of this owner carry: a building's depth one more than this one's,
        /// saturating at <see cref="int.MaxValue"/>, and anything else unchanged (false, 0).
        /// </summary>
        public BuildingLineage ChildOfSplit()
        {
            if (!IsBuildingDerived)
            {
                return NotBuilding;
            }

            return new BuildingLineage(true, SplitDepth == int.MaxValue ? int.MaxValue : SplitDepth + 1);
        }

        /// <summary>For tests of the saturation only: a building lineage at the depth given.</summary>
        internal static BuildingLineage BuildingAt(int splitDepth)
        {
            if (splitDepth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(splitDepth));
            }

            return new BuildingLineage(true, splitDepth);
        }

        public bool Equals(BuildingLineage other)
        {
            return IsBuildingDerived == other.IsBuildingDerived && SplitDepth == other.SplitDepth;
        }

        public override bool Equals(object obj)
        {
            return obj is BuildingLineage other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (IsBuildingDerived ? 1 : 0) ^ (SplitDepth << 1);
        }

        public override string ToString()
        {
            return IsBuildingDerived ? "building depth " + SplitDepth : "not building";
        }
    }

    /// <summary>
    /// The three settings of the building World D6 (DESIGN 7.2.2): the first split's horizontal distance limit, its
    /// angle limit, and the one ratio both shrink by per depth. <c>L(d) = L1 * r^(d-1)</c>,
    /// <c>A(d) = A1 * r^(d-1)</c> for a depth <c>d &gt;= 1</c>. These are the requested limits: no product minimum, no
    /// table, no second ratio. What a joint reports back can differ by axis (DESIGN 7.2.2): the current Unity/PhysX
    /// keeps a swing (joint Y, Z) limit at 3 degrees at least, while twist (joint X) keeps the requested range. That
    /// is the engine's, accepted as it is; nothing here raises a request to it.
    /// </summary>
    [Serializable]
    public struct BuildingWorldD6Settings
    {
        public BuildingWorldD6Settings(float firstLimitMetres, float firstAngleDegrees, float ratio)
        {
            this.firstLimitMetres = firstLimitMetres;
            this.firstAngleDegrees = firstAngleDegrees;
            this.ratio = ratio;
        }

        /// <summary>L1: the horizontal distance limit of a first-split child, in metres.</summary>
        public float firstLimitMetres;

        /// <summary>A1: the symmetric angle limit of a first-split child, in degrees.</summary>
        public float firstAngleDegrees;

        /// <summary>r: the common ratio per depth, strictly between 0 and 1.</summary>
        public float ratio;

        /// <summary>
        /// The adopted values (DESIGN 7.2.2, decided 2026-09-28): one metre and thirty degrees for a first-split child,
        /// halved at every further depth. They replace the earlier provisional quarter metre and fifteen degrees.
        /// </summary>
        public static BuildingWorldD6Settings Adopted => new BuildingWorldD6Settings(1f, 30f, 0.5f);

        /// <summary>
        /// Finite, a non-negative distance, an angle a joint limit can take (0 to 180 degrees), and a ratio strictly
        /// between 0 and 1.
        /// </summary>
        public bool IsValid =>
            float.IsFinite(firstLimitMetres) && firstLimitMetres >= 0f
            && float.IsFinite(firstAngleDegrees) && firstAngleDegrees >= 0f && firstAngleDegrees <= 180f
            && float.IsFinite(ratio) && ratio > 0f && ratio < 1f;

        /// <summary>The distance limit at a depth of at least 1: finite and not negative, 0 once it underflows.</summary>
        public float LimitMetres(int depth)
        {
            return Scaled(firstLimitMetres, depth);
        }

        /// <summary>The angle limit at a depth of at least 1, in degrees: finite and not negative.</summary>
        public float AngleDegrees(int depth)
        {
            return Scaled(firstAngleDegrees, depth);
        }

        private float Scaled(float first, int depth)
        {
            if (depth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(depth), "a building D6 is only made for a split child");
            }

            // In double, where r^(d-1) for any int depth is finite; it reaches 0 rather than a denormal float.
            double value = first * Math.Pow(ratio, depth - 1.0);
            var result = (float)value;
            return float.IsFinite(result) && result > 0f ? result : 0f;
        }
    }

    /// <summary>
    /// The `BuildingWorldD6Constraint` of DESIGN 7.2.2: a <see cref="ConfigurableJoint"/> from one anchor-less dynamic
    /// building child to the World. World Y is free; the two horizontal axes share one distance limit and all three
    /// rotations one symmetric angle limit, both from the child's depth. Its reference is where the child stands when
    /// it is published, so it starts with no relative translation or rotation.
    /// <para>
    /// **Not the sibling constraint.** <see cref="ProvisionalSeparation"/> connects the two siblings of a Provisional
    /// pair and ends at the Final handoff; this connects one child to the World and lives as long as that child's
    /// actor, across the handoff. They are made, kept and ended separately.
    /// </para>
    /// <para>
    /// No drive, spring, damper, projection, break or product minimum limit, and nothing switches to Locked: a limit
    /// that has shrunk to 0 stays a Limited motion with a 0 request. Each angle limit is given A(d) as it is; where the
    /// engine keeps a swing limit above that (see <see cref="BuildingWorldD6Settings"/>), nothing here raises,
    /// corrects or checks it. Once published, nothing of it is changed — not at the Final handoff and not when the
    /// child's mass properties are replaced.
    /// </para>
    /// </summary>
    internal static class BuildingWorldD6
    {
        private static readonly ProfilerMarker CreateMarker = new ProfilerMarker("Zantetsu.BuildingWorldD6.Create");

        /// <summary>
        /// Adds the constraint to an inactive side and configures everything that does not depend on where the side
        /// stands, then places it where the side is now. The side is out of the scene, so nothing of this enters the
        /// physics scene before the side is published.
        /// </summary>
        internal static ConfigurableJoint Create(PhysicsOwnerSide side, in BuildingWorldD6Settings settings, int depth)
        {
            using (CreateMarker.Auto())
            {
                float limit = settings.LimitMetres(depth);
                float angle = settings.AngleDegrees(depth);

                ConfigurableJoint joint = side.Root.AddComponent<ConfigurableJoint>();

                // To the World: no connected body, so the connected anchor is a world position.
                joint.connectedBody = null;
                joint.autoConfigureConnectedAnchor = false;
                joint.anchor = Vector3.zero;

                // Joint X and Z are the world's horizontal axes and joint Y the world's vertical (see Place).
                joint.xMotion = ConfigurableJointMotion.Limited;
                joint.yMotion = ConfigurableJointMotion.Free;
                joint.zMotion = ConfigurableJointMotion.Limited;
                joint.linearLimit = new SoftJointLimit { limit = limit, bounciness = 0f, contactDistance = 0f };

                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.lowAngularXLimit = new SoftJointLimit { limit = -angle, bounciness = 0f, contactDistance = 0f };
                joint.highAngularXLimit = new SoftJointLimit { limit = angle, bounciness = 0f, contactDistance = 0f };
                joint.angularYLimit = new SoftJointLimit { limit = angle, bounciness = 0f, contactDistance = 0f };
                joint.angularZLimit = new SoftJointLimit { limit = angle, bounciness = 0f, contactDistance = 0f };

                // A fresh component: no drive, spring, damper, projection or break is set, and none is used.
                side.BuildingWorld = joint;
                Place(side);
                return joint;
            }
        }

        /// <summary>
        /// Makes the side's pose now the constraint's reference: the connected anchor is the side's world position,
        /// and the joint's axes are the world's, expressed in the side's own frame. It is called only while the side is
        /// out of the scene — when it is built and when it is put where the source stands just before it is published
        /// — so the constraint enters the scene with no relative translation or rotation. It is never called on a
        /// published side.
        /// </summary>
        internal static void Place(PhysicsOwnerSide side)
        {
            ConfigurableJoint joint = side.BuildingWorld;
            if (joint == null)
            {
                return;
            }

            side.Root.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Quaternion toLocal = Quaternion.Inverse(rotation);
            joint.axis = toLocal * Vector3.right;
            joint.secondaryAxis = toLocal * Vector3.up;
            joint.connectedAnchor = position;
        }

        /// <summary>How many sides of a split get a constraint: the anchor-less ones of a building child, else none.</summary>
        internal static int Needed(in BuildingLineage child, bool positiveFixed, bool negativeFixed)
        {
            if (!child.IsBuildingDerived)
            {
                return 0;
            }

            return (positiveFixed ? 0 : 1) + (negativeFixed ? 0 : 1);
        }
    }
}
