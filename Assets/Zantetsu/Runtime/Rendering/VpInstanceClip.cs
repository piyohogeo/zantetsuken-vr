using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Which side of one cut plane an instance keeps, and how far it is moved apart, for the provisional display of a
    /// cut whose geometry is not settled yet (DESIGN 5.1). One of these per logical instance, uploaded beside the
    /// instance's object-to-world transform and read by both the forward and the shadow caster pass, so a fragment is
    /// clipped and offset the same way in every pass of one draw.
    /// <para>
    /// **The same parent geometry is drawn twice.** Nothing is duplicated, split or re-meshed for this: the positive
    /// and the negative display both point at the parent's own vertices and indices, and each simply discards the half
    /// it does not keep. The logical kerf is zero — the plane is not nudged to open a gap — so a gap appears only
    /// because the free side was moved (DESIGN 5.1: 論理上の切断幅（Kerf）は0とし…相対移動した結果としてのみ隙間と
    /// 断面が見える).
    /// </para>
    /// <para>
    /// **Coordinate spaces.** The plane is <c>(n.xyz, d)</c> with <c>dot(n, x) + d = 0</c> **in world space**, and the
    /// side test is made on the instance's world position **before** <see cref="offset"/> is added — moving a fragment
    /// apart must not change which part of it survives. The offset is also in world space and is added **after** the
    /// object-to-world transform, as DESIGN 5.1 requires; adding it before would let the transform rotate or scale the
    /// separation direction.
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
    /// This is one plane. DESIGN 5.2 selects up to <c>TemporaryClipPlaneCapacity = 8</c> of them and evaluates them
    /// with <c>SV_ClipDistance</c>, which is what the shaders here do with the single plane; plane selection, overflow
    /// and the ignored set are not part of this.
    /// </para>
    /// </summary>
    public readonly struct VpInstanceClip
    {
        /// <summary>The record for an instance that is not clipped and not moved: the ordinary display.</summary>
        public static VpInstanceClip None => default;

        /// <summary>Keeps the half of <paramref name="worldPlane"/> that <paramref name="side"/> names, moved by <paramref name="worldOffset"/>.</summary>
        /// <param name="worldPlane">The adopted plane in world space, as <c>(n.xyz, d)</c>.</param>
        /// <param name="side">Positive keeps <c>dot(n, x) + d &gt;= 0</c>, negative keeps the other half. Zero is no clipping.</param>
        /// <param name="worldOffset">The separation, in world space, added after the object-to-world transform.</param>
        public static VpInstanceClip Keep(Vector4 worldPlane, float side, Vector3 worldOffset)
        {
            return new VpInstanceClip(worldPlane, new Vector4(worldOffset.x, worldOffset.y, worldOffset.z, Mathf.Sign(side) * (side == 0f ? 0f : 1f)));
        }

        private VpInstanceClip(Vector4 plane, Vector4 offsetAndSide)
        {
            this.plane = plane;
            this.offsetAndSide = offsetAndSide;
        }

        /// <summary>The cut plane in world space, <c>(n.xyz, d)</c>. Ignored when the side is zero.</summary>
        public readonly Vector4 plane;

        /// <summary>The world-space separation in xyz, and in w the side kept: +1, -1, or 0 for no clipping.</summary>
        public readonly Vector4 offsetAndSide;

        /// <summary>The world offset this instance is drawn at.</summary>
        public Vector3 Offset => new Vector3(offsetAndSide.x, offsetAndSide.y, offsetAndSide.z);

        /// <summary>+1, -1, or 0 when this instance is not clipped.</summary>
        public float Side => offsetAndSide.w;

        /// <summary>Whether this instance is clipped at all.</summary>
        public bool IsClipped => offsetAndSide.w != 0f;
    }
}
