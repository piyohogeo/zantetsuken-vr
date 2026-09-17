using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One cut plane and the side of it an instance keeps: the half-space <c>side * (dot(n, x) + d) &gt;= 0</c> in
    /// world space. A set of these is what <see cref="VpInstanceClip.TryKeep"/> takes; the record it builds keeps
    /// the planes with the side already folded in.
    /// </summary>
    public readonly struct VpClipHalfSpace
    {
        /// <param name="worldPlane">The adopted plane in world space, as <c>(n.xyz, d)</c>.</param>
        /// <param name="side">Positive keeps <c>dot(n, x) + d &gt;= 0</c>, negative keeps the other half.</param>
        public VpClipHalfSpace(Vector4 worldPlane, float side)
        {
            plane = worldPlane;
            this.side = side;
        }

        /// <summary>The cut plane in world space, <c>(n.xyz, d)</c>.</summary>
        public readonly Vector4 plane;

        /// <summary>Which half of <see cref="plane"/> the instance keeps: positive or negative.</summary>
        public readonly float side;
    }

    /// <summary>
    /// Which parts of the cut planes an instance keeps, and how far it is moved apart, for the provisional display of
    /// a cut whose geometry is not settled yet (DESIGN 5.1). One of these per logical instance, uploaded beside the
    /// instance's object-to-world transform and read by both the forward and the shadow caster pass, so a fragment is
    /// clipped and offset the same way in every pass of one draw.
    /// <para>
    /// **Fixed capacity.** The record holds <see cref="PlaneCapacity"/> = 8 planes, a count of how many of them are
    /// valid, and **one** world offset common to all of them — the size and the shape never vary with the count
    /// (DESIGN 5.2 <c>TemporaryClipPlaneCapacity = 8</c>). The surviving region is the **intersection** of the valid
    /// half-spaces: a fragment is kept only where every one of them keeps it. Each plane is stored with its side
    /// already folded in, so what the shader evaluates is <c>dot(n, x) + d &gt;= 0</c> for each valid plane; beyond
    /// the count it uses a positive constant instead, which is what DESIGN 5.2 asks of the unused components at
    /// every vertex. A zeroed record — <see cref="None"/> or <c>default</c> — is a count of zero and therefore
    /// clips nothing.
    /// </para>
    /// <para>
    /// **One drawing implementation.** Omitting the clips, <see cref="None"/> and the single-plane <see cref="Keep"/>
    /// keep exactly the meanings they had: they are thin conversions into this one form, a count of zero or one, and
    /// there is no second path, material, keyword, pass or draw for them. A count above the capacity is a different
    /// matter: <see cref="TryKeep"/> refuses the whole set before anything is updated and never quietly keeps the
    /// first eight, because a silently truncated intersection is a larger region than the caller asked for.
    /// </para>
    /// <para>
    /// **The same parent geometry is drawn once per side.** Nothing is duplicated, split or re-meshed for this: every
    /// side's display points at the parent's own vertices and indices, and each simply discards what it does not
    /// keep. The logical kerf is zero — no plane is nudged to open a gap — so a gap appears only because a free side
    /// was moved (DESIGN 5.1: 論理上の切断幅（Kerf）は0とし…相対移動した結果としてのみ隙間と断面が見える).
    /// </para>
    /// <para>
    /// **Coordinate spaces.** Every plane is <c>(n.xyz, d)</c> with <c>dot(n, x) + d = 0</c> **in world space**, and
    /// every side test is made on the instance's world position **before** <see cref="Offset"/> is added — moving a
    /// fragment apart must not change which part of it survives. The offset is also in world space and is added
    /// **once, after** the object-to-world transform, as DESIGN 5.1 requires; adding it before would let the
    /// transform rotate or scale the separation direction.
    /// </para>
    /// <para>
    /// **Fixity is the caller's.** Whether a side is fixed, and therefore takes a zero offset, is decided from the
    /// owner's anchors by whoever prepares these records (DESIGN 7.1); nothing here classifies anchors, and the
    /// renderer has no dependency on the cut or the ledger. A side being fixed is never a reason to skip its clipped
    /// display (DESIGN 5.1: 固定Anchorの有無を仮描画の省略条件にしない), and neither is a side whose emptiness is not
    /// settled yet: the provisional display starts without waiting for that judgement (Geometry未確定の間は…空判定の
    /// 完了を表示開始条件にしない). A side **known** to be empty is a different matter and keeps its own contract — no
    /// renderer is made for it at all — which nothing here softens.
    /// </para>
    /// <para>
    /// **Not here.** Which planes an instance ends up with — DESIGN 5.2's selection out of the admitted cuts, the
    /// ancestor-first order, what happens when more than the capacity apply and what the ignored set then means — is
    /// no part of this record. It takes the set it is given, or refuses it.
    /// </para>
    /// </summary>
    public readonly struct VpInstanceClip
    {
        /// <summary>DESIGN 5.2 <c>TemporaryClipPlaneCapacity</c>: how many planes one instance can carry.</summary>
        public const int PlaneCapacity = 8;

        /// <summary>The record for an instance that is not clipped and not moved: the ordinary display.</summary>
        public static VpInstanceClip None => default;

        /// <summary>
        /// The single-plane spelling, unchanged: keeps the half of <paramref name="worldPlane"/> that
        /// <paramref name="side"/> names, moved by <paramref name="worldOffset"/>. A side of zero is no clipping —
        /// the instance is still moved by the offset, as it always was — and one plane cannot exceed the capacity, so
        /// this cannot fail.
        /// </summary>
        /// <param name="worldPlane">The adopted plane in world space, as <c>(n.xyz, d)</c>.</param>
        /// <param name="side">Positive keeps <c>dot(n, x) + d &gt;= 0</c>, negative keeps the other half. Zero is no clipping.</param>
        /// <param name="worldOffset">The separation, in world space, added after the object-to-world transform.</param>
        public static VpInstanceClip Keep(Vector4 worldPlane, float side, Vector3 worldOffset)
        {
            if (side == 0f)
            {
                return new VpInstanceClip(
                    Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero,
                    Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero,
                    OffsetAndCount(worldOffset, 0));
            }

            return new VpInstanceClip(
                Signed(worldPlane, side), Vector4.zero, Vector4.zero, Vector4.zero,
                Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero,
                OffsetAndCount(worldOffset, 1));
        }

        /// <summary>
        /// Keeps the intersection of <paramref name="halfSpaces"/>, moved by <paramref name="worldOffset"/>: up to
        /// <see cref="PlaneCapacity"/> planes, each with the side this instance keeps, and one offset for all of
        /// them. An empty set is accepted and clips nothing, which is <see cref="None"/> with an offset.
        /// <para>
        /// Refuses, building nothing and leaving <paramref name="clip"/> clipping nothing, when the set is null, when
        /// it holds **more than the capacity** — the whole set is refused, never truncated — or when any half-space
        /// has a side of zero, since dropping a constraint from a set would quietly widen the region that survives.
        /// </para>
        /// <para>
        /// On a refusal the output is <see cref="None"/> — passing the same variable in again replaces whatever it
        /// held, so the output is no copy of an earlier record. What this does not do is update the GPU: it touches
        /// no draw data at all. Adopting a candidate and uploading it only when this succeeds is therefore how the
        /// caller keeps the display it already has.
        /// </para>
        /// <para>
        /// Whether the planes are finite and normalized is the caller's, as it is for <see cref="Keep"/>: these are
        /// the planes the cut adopted, carried through unchanged.
        /// </para>
        /// </summary>
        public static bool TryKeep(IReadOnlyList<VpClipHalfSpace> halfSpaces, Vector3 worldOffset, out VpInstanceClip clip)
        {
            clip = None;
            if (halfSpaces == null)
            {
                return false;
            }

            int count = halfSpaces.Count;
            if (count > PlaneCapacity)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (halfSpaces[i].side == 0f)
                {
                    return false;
                }
            }

            clip = new VpInstanceClip(
                At(halfSpaces, 0), At(halfSpaces, 1), At(halfSpaces, 2), At(halfSpaces, 3),
                At(halfSpaces, 4), At(halfSpaces, 5), At(halfSpaces, 6), At(halfSpaces, 7),
                OffsetAndCount(worldOffset, count));
            return true;
        }

        private VpInstanceClip(
            Vector4 plane0, Vector4 plane1, Vector4 plane2, Vector4 plane3,
            Vector4 plane4, Vector4 plane5, Vector4 plane6, Vector4 plane7,
            Vector4 offsetAndCount)
        {
            _plane0 = plane0;
            _plane1 = plane1;
            _plane2 = plane2;
            _plane3 = plane3;
            _plane4 = plane4;
            _plane5 = plane5;
            _plane6 = plane6;
            _plane7 = plane7;
            _offsetAndCount = offsetAndCount;
        }

        // The GPU layout, in this order: eight signed planes and then the offset with the valid count, 144 bytes.
        // VpIndexedIndirectDrawBatch.InstanceClipStride and the VpInstanceClip struct of both shaders match it.
        private readonly Vector4 _plane0;
        private readonly Vector4 _plane1;
        private readonly Vector4 _plane2;
        private readonly Vector4 _plane3;
        private readonly Vector4 _plane4;
        private readonly Vector4 _plane5;
        private readonly Vector4 _plane6;
        private readonly Vector4 _plane7;
        private readonly Vector4 _offsetAndCount;

        /// <summary>How many of the <see cref="PlaneCapacity"/> planes are valid: 0 to 8.</summary>
        public int PlaneCount => (int)_offsetAndCount.w;

        /// <summary>The world offset this instance is drawn at, common to all of its planes.</summary>
        public Vector3 Offset => new Vector3(_offsetAndCount.x, _offsetAndCount.y, _offsetAndCount.z);

        /// <summary>Whether this instance is clipped at all, that is, whether it carries any plane.</summary>
        public bool IsClipped => PlaneCount > 0;

        /// <summary>
        /// One valid plane, with the side folded in: the half kept is <c>dot(n, x) + d &gt;= 0</c>. Indices from zero
        /// to <see cref="PlaneCount"/> - 1; anything else is out of range, since the planes past the count hold no
        /// constraint at all.
        /// </summary>
        public Vector4 SignedPlane(int index)
        {
            if (index < 0 || index >= PlaneCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "this record carries " + PlaneCount + " planes");
            }

            switch (index)
            {
                case 0: return _plane0;
                case 1: return _plane1;
                case 2: return _plane2;
                case 3: return _plane3;
                case 4: return _plane4;
                case 5: return _plane5;
                case 6: return _plane6;
                default: return _plane7;
            }
        }

        /// <summary>The plane with its side folded in, so that the half kept is the non-negative one.</summary>
        private static Vector4 Signed(Vector4 worldPlane, float side)
        {
            return side > 0f ? worldPlane : -worldPlane;
        }

        private static Vector4 At(IReadOnlyList<VpClipHalfSpace> halfSpaces, int index)
        {
            if (index >= halfSpaces.Count)
            {
                // Past the valid count nothing reads these, and a zero plane keeps a zeroed record meaning
                // "no clipping" whichever way it was made.
                return Vector4.zero;
            }

            VpClipHalfSpace halfSpace = halfSpaces[index];
            return Signed(halfSpace.plane, halfSpace.side);
        }

        private static Vector4 OffsetAndCount(Vector3 worldOffset, int count)
        {
            return new Vector4(worldOffset.x, worldOffset.y, worldOffset.z, count);
        }
    }
}
