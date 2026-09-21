using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>What a display was told about where one fragment's shape stands.</summary>
    public enum VpFragmentPlacementKind
    {
        /// <summary>
        /// This fragment is not part of a following arrangement: its registration's own placement is where it is
        /// drawn, and that is the answer rather than the absence of one.
        /// </summary>
        Static = 0,

        /// <summary>It follows something, and that is where it stands now.</summary>
        Following = 1,

        /// <summary>
        /// It follows something and where that is was not said. A shape drawn at the placement it was registered with
        /// would be drawn where it used to be, so this is refused instead. It is the caller's to avoid by answering
        /// for every fragment it says follows something.
        /// </summary>
        Missing = 2,
    }

    /// <summary>
    /// Where a logical fragment's shape stands, when that fragment follows something of its own.
    /// <para>
    /// A registration places the geometry it holds. That is enough while everything drawn from it moves together, and
    /// it stops being enough once the sides of a published cut move apart: they share the ancestor's geometry but not
    /// its placement. This is how a display asks, for one fragment, where that fragment's shape is now.
    /// </para>
    /// <para>
    /// **The three answers are kept apart.** A fragment of an arrangement that does not follow anything is
    /// <see cref="VpFragmentPlacementKind.Static"/> and is drawn where it was registered, which is what that
    /// arrangement means. A fragment that follows something answers with where it is. One that follows something and
    /// cannot say where is <see cref="VpFragmentPlacementKind.Missing"/>, and is refused rather than drawn at a
    /// placement that is no longer its own — the two are not the same answer and are not made into one here.
    /// </para>
    /// <para>
    /// **A side of an accepted cut is asked about as that side.** It has no fragment of its own -- none is issued
    /// before publication -- so the two sides of one accepted cut come here under the same fragment, named apart by
    /// the cut and the side. A caller that has something for each side answers for each; one that has nothing for
    /// either answers for the fragment, as it did before, and the two sides get the same answer. Answering for one
    /// side out of a pair by handing back the other's place, or the place the fragment had before the pair existed,
    /// is what <see cref="VpFragmentPlacementKind.Missing"/> is for.
    /// </para>
    /// <para>
    /// **What is asked for.** The base placement of the geometry's own local frame: where the shape is drawn. The
    /// display adds nothing to it — no cut displaces a side for the display (DESIGN 5.1).
    /// </para>
    /// <para>
    /// It is read while a snapshot is built and not again while that snapshot draws, so what one collection decided
    /// stays as it was decided.
    /// </para>
    /// </summary>
    public interface IVpFragmentPlacement
    {
        /// <summary>
        /// Where the shape of one branch stands, and whether it stands anywhere of its own.
        /// </summary>
        /// <param name="fragment">The live fragment the branch is of.</param>
        /// <param name="operation">
        /// The accepted cut this branch is a side of, when it is one; unset when the branch is drawn whole. It is
        /// never a published cut: a published one has children, and each child is a fragment that answers for itself.
        /// </param>
        /// <param name="side">+1 or -1 for a side of that cut, 0 for a branch drawn whole.</param>
        VpFragmentPlacementKind TryGetGeometryLocalToWorld(
            LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld);
    }
}
