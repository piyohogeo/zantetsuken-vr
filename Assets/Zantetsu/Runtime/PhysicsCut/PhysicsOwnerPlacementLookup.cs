using System;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Where each live fragment's display geometry stands, answered from the physics owners themselves
    /// (DESIGN 5.6, 7.1.2). This is the one piece that joins the fragment-to-owner correspondence to the display's
    /// placement lookup, and it is all it does: it holds no correspondence of its own, keeps no state, invents no
    /// identity, and drives nothing. A display asks; this reads the owner that fragment has at that moment.
    /// <para>
    /// **What it gives back is a base placement**: the owner's world transform with that lineage's
    /// geometry-to-owner correspondence, and nothing else. The display's separation offsets and what a geometry
    /// commit folded into a geometry's own frame stay the display's, summed and carried where they already are, so a
    /// physical position and a display separation are never added to one another and never applied twice. The cut
    /// DAG's lineage-to-geometry mapping is a different thing again — that one decides the plane a kernel cuts at —
    /// and is not mixed in here.
    /// </para>
    /// <para>
    /// **A gap is never dressed up as an arrangement.** An owner **in the scene** that says where its display
    /// geometry sits is followed; one in the scene that says nothing of the kind is drawn where it was registered,
    /// which is that owner's statement about its display and not a fallback. Everything else is refused: a fragment
    /// with no owner at all, and one whose owner has been withdrawn, are both gaps, whether or not a correspondence
    /// was ever given, so that nothing is drawn where it used to be.
    /// </para>
    /// <para>
    /// **Nothing here is read while drawing.** A display reads this when it collects, settles a snapshot from it and
    /// draws that; an owner that moves afterwards moves what the next collection is built from, never what has
    /// already been settled.
    /// </para>
    /// </summary>
    public sealed class PhysicsOwnerPlacementLookup : IVpFragmentPlacement
    {
        private readonly PhysicsOwnerRegistry _registry;

        public PhysicsOwnerPlacementLookup(PhysicsOwnerRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public VpFragmentPlacementKind TryGetGeometryLocalToWorld(
            LogicalFragmentId fragment, out Matrix4x4 geometryLocalToWorld)
        {
            geometryLocalToWorld = default;

            // Whether there is an owner in the scene at all comes first. A fragment with no physics, and one whose
            // owner has left the scene, are both refused: where it was registered is where it was, and an
            // arrangement is something an owner in use states, not something a gap is read as.
            if (!_registry.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.Root == null)
            {
                return VpFragmentPlacementKind.Missing;
            }

            if (owner.GeometryLocalToOwner == null)
            {
                // An owner in use whose display is arranged some other way, which is what having no correspondence
                // means: its fragment is drawn where it was registered, and that is the answer.
                return VpFragmentPlacementKind.Static;
            }

            return owner.TryReadGeometryLocalToWorld(out geometryLocalToWorld)
                ? VpFragmentPlacementKind.Following
                : VpFragmentPlacementKind.Missing;
        }
    }
}
