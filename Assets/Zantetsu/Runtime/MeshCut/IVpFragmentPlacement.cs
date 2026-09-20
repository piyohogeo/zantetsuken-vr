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
    /// placement that is no longer its own — the two are not the same answer and are not made into one here. A side
    /// of a cut that has only been accepted is not a fragment of its own yet; its branch is its source, which answers
    /// for itself.
    /// </para>
    /// <para>
    /// **What is asked for.** The base placement of the geometry's own local frame: where the shape would be drawn
    /// with no temporary separation, and without what earlier commits folded into that geometry — the display adds
    /// both itself (DESIGN 5.1).
    /// </para>
    /// <para>
    /// It is read while a snapshot is built and not again while that snapshot draws, so what one collection decided
    /// stays as it was decided.
    /// </para>
    /// </summary>
    public interface IVpFragmentPlacement
    {
        /// <summary>Where <paramref name="fragment"/>'s shape stands, and whether it stands anywhere of its own.</summary>
        VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, out Matrix4x4 geometryLocalToWorld);
    }
}
