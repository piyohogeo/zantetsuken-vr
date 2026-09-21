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
    /// geometry-to-owner correspondence, and nothing else. That is the whole of where the shape is drawn — the
    /// display adds nothing to it, and a geometry commit takes nothing into it (DESIGN 5.1). The cut DAG's
    /// lineage-to-geometry mapping is a different thing again — that one decides the plane a kernel cuts at — and is
    /// not mixed in here.
    /// </para>
    /// <para>
    /// **A published Provisional pair is answered side by side** (DESIGN 7.1.1). Its two actors move apart while the
    /// fragment they stand in for is not split, so a caller asking about one side of that accepted cut is answered
    /// from that side's actor. Asking about the same fragment **without** naming a side — or naming a different cut —
    /// is <see cref="VpFragmentPlacementKind.Missing"/> while the pair is there: the body as a whole is not standing
    /// anywhere any more, and neither half of it, nor the withdrawn source, is handed back in its place. A fragment
    /// with no pair is answered exactly as it was before, side or no side.
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
            LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
        {
            geometryLocalToWorld = default;

            // A published pair stands in for this fragment, so it is what answers -- by side. Nothing else of this
            // fragment is in the scene while it is there.
            if (_registry.TryGetProvisionalOf(fragment, out ProvisionalOwnerPair pair) && !pair.IsEnded)
            {
                bool names = operation.IsSet && operation.Equals(pair.Operation) && (side > 0f || side < 0f);
                if (!names)
                {
                    // Asked about the body as a whole, or about some other cut of it. There is no one answer: the two
                    // sides stand in different places, and the source has left the scene. Choosing a half, or the
                    // place the body had before it was replaced, would draw it where it is not.
                    return VpFragmentPlacementKind.Missing;
                }

                bool positive = side > 0f;
                if (!pair.IsStanding(positive))
                {
                    return VpFragmentPlacementKind.Missing;
                }

                if (pair.GeometryLocalToOwner == null)
                {
                    // A pair whose display is arranged some other way, which is what having no correspondence means.
                    return VpFragmentPlacementKind.Static;
                }

                return pair.TryReadGeometryLocalToWorld(positive, out geometryLocalToWorld)
                    ? VpFragmentPlacementKind.Following
                    : VpFragmentPlacementKind.Missing;
            }

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
