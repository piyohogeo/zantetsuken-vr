using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The adopted face of one admitted cut, as the compatibility grouping identifies it: the operation, together with
    /// the ledger that issued it.
    /// <para>
    /// **The ledger is part of the identity.** Operation numbers are unique only within one ledger, so the same number
    /// issued by two ledgers is two faces. The ledger is compared by reference.
    /// </para>
    /// <para>
    /// **This is not the CutPlaneId contract.** Today an operation adopts exactly one face, so the operation stands for
    /// that face. The adopted face of an operation is not the <c>SourceSlashPlane</c> it came from: two targets cut by
    /// one slash have two operations and so two faces here, whatever their planes. The operation keeps its id when it
    /// is published, so a face keeps its identity from pending to published.
    /// </para>
    /// </summary>
    public readonly struct VpCapFace : IEquatable<VpCapFace>
    {
        public VpCapFace(LogicalCutLedger scope, CutOperationId operation)
        {
            this.scope = scope;
            this.operation = operation;
        }

        /// <summary>The ledger that issued <see cref="operation"/>.</summary>
        public readonly LogicalCutLedger scope;

        /// <summary>The admitted cut whose adopted face this is.</summary>
        public readonly CutOperationId operation;

        public bool IsSet => scope != null && operation.IsSet;

        public bool Equals(VpCapFace other) => ReferenceEquals(scope, other.scope) && operation == other.operation;
        public override bool Equals(object obj) => obj is VpCapFace other && Equals(other);
        public override int GetHashCode() => operation.GetHashCode();
        public static bool operator ==(VpCapFace a, VpCapFace b) => a.Equals(b);
        public static bool operator !=(VpCapFace a, VpCapFace b) => !a.Equals(b);
    }

    /// <summary>
    /// One cut condition a drawn target is under: which face, which half of it the target keeps, and where that face
    /// is in world space now.
    /// </summary>
    public readonly struct VpCapConstraint
    {
        public VpCapConstraint(VpCapFace face, float side, Vector4 worldPlane)
        {
            this.face = face;
            this.side = side;
            this.worldPlane = worldPlane;
        }

        public readonly VpCapFace face;

        /// <summary>+1 keeps <c>dot(n, x) + d &gt;= 0</c> of <see cref="worldPlane"/>, -1 the other half.</summary>
        public readonly float side;

        /// <summary>
        /// The face's current world plane <c>(n.xyz, d)</c>, before any separation, n normalized. It is compared
        /// component by component as given; the sign convention is the face's own and is never flipped here.
        /// </summary>
        public readonly Vector4 worldPlane;
    }

    /// <summary>
    /// One drawn target to group: **every** cut condition it is under, in any order, and the one separation it is drawn
    /// at. Every condition counts, including a face whose cap the visibility test left out — whether a cap is seen
    /// is not part of what the stencil of this target has to share.
    /// <para>
    /// The conditions are read, never copied: made from a list, the target reads that list itself, so a change the
    /// caller makes to it afterwards is what the next judgement sees, as it always was; made from a
    /// <see cref="VpArrayRange{T}"/>, it reads that part of the range owner's array, for as long as the owner keeps it
    /// as it was. Both are judged by the same code.
    /// </para>
    /// </summary>
    public readonly struct VpCapCompatibilityTarget
    {
        /// <summary>A target that reads <paramref name="constraints"/> itself, not a copy; a null list stays null.</summary>
        public VpCapCompatibilityTarget(IReadOnlyList<VpCapConstraint> constraints, Vector3 offset)
        {
            this.constraints = new VpReadOnlyItems<VpCapConstraint>(constraints);
            this.offset = offset;
        }

        /// <summary>A target that reads its conditions from <paramref name="constraints"/>, copying nothing.</summary>
        public VpCapCompatibilityTarget(VpArrayRange<VpCapConstraint> constraints, Vector3 offset)
        {
            this.constraints = new VpReadOnlyItems<VpCapConstraint>(constraints);
            this.offset = offset;
        }

        public readonly VpReadOnlyItems<VpCapConstraint> constraints;

        /// <summary>The separation, in world space, the target is drawn at.</summary>
        public readonly Vector3 offset;
    }

    /// <summary>
    /// The stencil compatibility groups of DESIGN 5.6 / T-067, as a CPU classification only: which targets may share
    /// one stencil accumulation because they are under the same cut conditions now.
    /// <para>
    /// **Compatible** means: the same number of conditions, the same set of faces — matched by identity, so the order
    /// they are given in does not matter — and for every face the same side and a current world plane within
    /// <c>planeEpsilon</c> in each of n.x, n.y, n.z and d; and offsets within <c>offsetEpsilon</c> in each component.
    /// A face one target has and the other does not, an extra boundary included, makes them incompatible. Nothing else
    /// is read: not the geometry's orientation or sign, not a colour, not a triangle count or winding capacity, and
    /// not visibility. No float is hashed: identities are compared as identities and floats only against the epsilons.
    /// </para>
    /// <para>
    /// **Grouping.** Closeness within an epsilon is not transitive, so a target joins a group only when it is
    /// compatible with **every** target already in it, and otherwise starts a new one. Targets are taken in the order
    /// given and each goes to the first group that takes it. The result is a partition in which every two members of
    /// one group are compatible; it is not claimed to have the fewest groups, and the group numbers mean nothing
    /// beyond the call. A target with a value that is not finite is compatible with nothing and stands alone.
    /// </para>
    /// <para>
    /// **A group is not a stencil colour** and its count is not a colour count or a draw count: colour assignment,
    /// residual-support conflicts and the final merged colour come after this and are not done here. Each call is
    /// judged from its input alone; nothing is kept between calls.
    /// </para>
    /// <para>
    /// The epsilons are the caller's. No product value exists for either (DESIGN O-034 leaves margins open).
    /// </para>
    /// </summary>
    public static class VpCapCompatibility
    {
        /// <summary>
        /// Groups <paramref name="targets"/>, writing each target's group into <paramref name="groupOfTarget"/> at
        /// the same index, and answers how many groups there are.
        /// </summary>
        /// <exception cref="ArgumentNullException">A list, or a target's conditions, is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="groupOfTarget"/> is shorter than the targets, a target has no condition, a condition has an
        /// unset face or a side that is not +1 or -1, or one target names the same face twice.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">An epsilon is negative or not finite.</exception>
        public static int Classify(
            IReadOnlyList<VpCapCompatibilityTarget> targets,
            float planeEpsilon,
            float offsetEpsilon,
            int[] groupOfTarget)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            if (groupOfTarget == null)
            {
                throw new ArgumentNullException(nameof(groupOfTarget));
            }

            if (groupOfTarget.Length < targets.Count)
            {
                throw new ArgumentException("There is a group slot for every target.", nameof(groupOfTarget));
            }

            CheckEpsilon(planeEpsilon, nameof(planeEpsilon));
            CheckEpsilon(offsetEpsilon, nameof(offsetEpsilon));
            for (int i = 0; i < targets.Count; i++)
            {
                CheckShape(targets[i], i);
            }

            int groups = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                int chosen = -1;
                for (int g = 0; g < groups && chosen < 0; g++)
                {
                    bool withAll = true;
                    for (int j = 0; j < i && withAll; j++)
                    {
                        if (groupOfTarget[j] == g)
                        {
                            withAll = AreCompatible(targets[i], targets[j], planeEpsilon, offsetEpsilon);
                        }
                    }

                    if (withAll)
                    {
                        chosen = g;
                    }
                }

                if (chosen < 0)
                {
                    chosen = groups;
                    groups++;
                }

                groupOfTarget[i] = chosen;
            }

            return groups;
        }

        /// <summary>
        /// Whether two targets are under the same cut conditions, as <see cref="Classify"/> decides it. Symmetric; not
        /// transitive.
        /// </summary>
        public static bool AreCompatible(
            in VpCapCompatibilityTarget a,
            in VpCapCompatibilityTarget b,
            float planeEpsilon,
            float offsetEpsilon)
        {
            CheckEpsilon(planeEpsilon, nameof(planeEpsilon));
            CheckEpsilon(offsetEpsilon, nameof(offsetEpsilon));
            CheckShape(a, 0);
            CheckShape(b, 1);

            if (a.constraints.Count != b.constraints.Count || !Near(a.offset, b.offset, offsetEpsilon))
            {
                return false;
            }

            // Faces are unique within a target, so matching each of a's to the one of b's with the same identity is
            // matching the two sets, whatever order either was given in.
            for (int i = 0; i < a.constraints.Count; i++)
            {
                VpCapConstraint mine = a.constraints[i];
                bool matched = false;
                for (int j = 0; j < b.constraints.Count && !matched; j++)
                {
                    VpCapConstraint theirs = b.constraints[j];
                    if (theirs.face != mine.face)
                    {
                        continue;
                    }

                    if (theirs.side != mine.side || !Near(theirs.worldPlane, mine.worldPlane, planeEpsilon))
                    {
                        return false;
                    }

                    matched = true;
                }

                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        private static void CheckEpsilon(float epsilon, string name)
        {
            if (float.IsNaN(epsilon) || float.IsInfinity(epsilon) || epsilon < 0f)
            {
                throw new ArgumentOutOfRangeException(name, epsilon, "An epsilon is a finite value, zero or more.");
            }
        }

        private static void CheckShape(in VpCapCompatibilityTarget target, int index)
        {
            VpReadOnlyItems<VpCapConstraint> constraints = target.constraints;
            if (constraints.IsNull)
            {
                throw new ArgumentNullException("targets", "Target " + index + " has no list of conditions.");
            }

            if (constraints.Count == 0)
            {
                throw new ArgumentException("Target " + index + " is under no cut condition.", "targets");
            }

            for (int c = 0; c < constraints.Count; c++)
            {
                VpCapConstraint constraint = constraints[c];
                if (!constraint.face.IsSet)
                {
                    throw new ArgumentException("Target " + index + " names an unset face.", "targets");
                }

                if (constraint.side != 1f && constraint.side != -1f)
                {
                    throw new ArgumentException("Target " + index + " keeps a side that is not +1 or -1.", "targets");
                }

                for (int d = 0; d < c; d++)
                {
                    if (constraints[d].face == constraint.face)
                    {
                        throw new ArgumentException("Target " + index + " names one face twice.", "targets");
                    }
                }
            }
        }

        /// <summary>Within the epsilon in every component. A component that is not finite is near nothing.</summary>
        private static bool Near(Vector4 a, Vector4 b, float epsilon)
        {
            return Near(a.x, b.x, epsilon) && Near(a.y, b.y, epsilon) && Near(a.z, b.z, epsilon) && Near(a.w, b.w, epsilon);
        }

        private static bool Near(Vector3 a, Vector3 b, float epsilon)
        {
            return Near(a.x, b.x, epsilon) && Near(a.y, b.y, epsilon) && Near(a.z, b.z, epsilon);
        }

        /// <summary>Value by value: Vector3's own == is approximate, and these two came from one variable.</summary>
        private static bool SameExactly(Vector3 a, Vector3 b)
        {
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }

        private static bool Near(float a, float b, float epsilon)
        {
            if (float.IsNaN(a) || float.IsInfinity(a) || float.IsNaN(b) || float.IsInfinity(b))
            {
                return false;
            }

            float difference = a - b;
            return !float.IsInfinity(difference) && Math.Abs(difference) <= epsilon;
        }

        // ----- the current single-cut display ---------------------------------------------------------------------

        /// <summary>
        /// The target of one prepared cap of <paramref name="display"/>, for the display as it is today: one body
        /// under **one** admitted cut, so the body's whole set of conditions is that one face, kept on this cap's side,
        /// at the separation this side is drawn at.
        /// <para>
        /// **This adapter is for the single-cut display only**, and it checks that it is in one: every side the
        /// display drew for the same body and side must carry exactly one clip plane at this cap's offset, or it
        /// refuses. It is not a rule that a cap's own face is enough. Once a body can be under several boundaries,
        /// its target is all of them — not only the face of the cap in hand, and not only the faces whose caps are
        /// visible — and that is the input <see cref="Classify"/> already takes.
        /// </para>
        /// <para>
        /// The face is the cap's operation under this display's own ledger. The plane and the offset are the ones
        /// the display settled from the placement it was given; nothing here moves anything.
        /// </para>
        /// </summary>
        public static bool TryGetSingleCutTarget(
            VpLogicalCutDisplay display,
            int capIndex,
            out VpCapCompatibilityTarget target)
        {
            return TryGetSingleCutTarget(display, capIndex, new VpCapConstraint[SingleCutConstraints], 0, out target);
        }

        /// <summary>How many conditions <see cref="TryGetSingleCutTarget(VpLogicalCutDisplay, int, VpCapConstraint[], int, out VpCapCompatibilityTarget)"/> writes: the single-cut display's one.</summary>
        internal const int SingleCutConstraints = 1;

        /// <summary>
        /// The same target, its conditions written into <paramref name="scratch"/> from <paramref name="start"/> on
        /// (<see cref="SingleCutConstraints"/> of them) and read from there: nothing is allocated, and the target is good
        /// for as long as the caller leaves that part of <paramref name="scratch"/> as it is.
        /// </summary>
        internal static bool TryGetSingleCutTarget(
            VpLogicalCutDisplay display,
            int capIndex,
            VpCapConstraint[] scratch,
            int start,
            out VpCapCompatibilityTarget target)
        {
            if (display == null)
            {
                throw new ArgumentNullException(nameof(display));
            }

            if (scratch == null)
            {
                throw new ArgumentNullException(nameof(scratch));
            }

            if (start < 0 || start > scratch.Length - SingleCutConstraints)
            {
                throw new ArgumentOutOfRangeException(nameof(start), "There is room for the target's conditions.");
            }

            target = default;
            if (!display.TryGetCapRecord(capIndex, out LogicalCutCapRecord record) || !record.operation.IsSet)
            {
                return false;
            }

            // The single-cut shape, confirmed on what the display drew for this body and side.
            int matching = 0;
            for (int i = 0; i < display.SideCount; i++)
            {
                if (!display.TryGetSide(i, out LogicalCutDisplaySide side)
                    || side.source != record.source
                    || side.side != record.side)
                {
                    continue;
                }

                if (side.operation != record.operation || side.clip.PlaneCount != 1 || !SameExactly(side.offset, record.offset))
                {
                    return false;
                }

                matching++;
            }

            if (matching == 0)
            {
                return false;
            }

            var face = new VpCapFace(display.Ledger, record.operation);
            scratch[start] = new VpCapConstraint(face, record.side, record.worldPlane);
            target = new VpCapCompatibilityTarget(
                new VpArrayRange<VpCapConstraint>(scratch, start, SingleCutConstraints), record.offset);
            return true;
        }
    }
}
