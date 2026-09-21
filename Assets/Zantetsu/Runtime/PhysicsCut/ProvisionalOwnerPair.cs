using System;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The **published** Provisional pair of one accepted cut (DESIGN 7.1.1): the two actors that are in the physics
    /// scene in place of their source, the shapes they share with it, and the constraint between them.
    /// <para>
    /// **It is kept on the cut, not on a fragment.** No logical child exists yet — the ledger publishes nothing for a
    /// Provisional — so there is no fragment to give either side to, and the source keeps the one owner record it has
    /// always had. The cut is what names this pair, by the <see cref="CutOperationId"/> the ledger already issued: no
    /// new identity, no new state of its own. A live fragment has at most one accepted cut (DESIGN 7.1), so a source
    /// has at most one of these.
    /// </para>
    /// <para>
    /// **What it owns**, from the moment it is published: the two objects, the constraint on one of them, the two
    /// shapes, and through those shapes the holds on whoever owns the collider meshes. It is the
    /// <see cref="ProvisionalOwnerCandidate"/>'s ownership, handed over at publication — the candidate is detached
    /// there and destroys nothing afterwards. An unpublished candidate that is simply disposed never becomes one of
    /// these: the two are not the same thing and are not disposed twice.
    /// </para>
    /// <para>
    /// **What it does not own**: the source, its owner record, its shape and the meshes themselves. The meshes are
    /// the source's suppliers' and are held here, not taken; ending this pair gives those holds back, and a mesh goes
    /// back only when nothing at all holds it.
    /// </para>
    /// <para>
    /// **Ending it is the correspondence's to do**, through
    /// <see cref="PhysicsOwnerRegistry.EndProvisional"/>: the pair is looked up by its cut, by its source and by each
    /// of its two bodies, and all three have to be forgotten at the moment the actors go. There is deliberately no way
    /// to end one from the outside on its own, which would leave those lookups naming something that is not there any
    /// more.
    /// </para>
    /// </summary>
    public sealed class ProvisionalOwnerPair
    {
        internal ProvisionalOwnerPair(
            CutOperationId operation,
            LogicalFragmentId source,
            PhysicsOwnerSide positive,
            PhysicsOwnerSide negative,
            PhysicsOwnerShape positiveShape,
            PhysicsOwnerShape negativeShape,
            ConfigurableJoint separation,
            Matrix4x4? geometryLocalToOwner)
        {
            Operation = operation;
            Source = source;
            Positive = positive;
            Negative = negative;
            PositiveShape = positiveShape;
            NegativeShape = negativeShape;
            Separation = separation;
            GeometryLocalToOwner = geometryLocalToOwner;
        }

        /// <summary>The accepted cut this pair stands for. It is the ledger's own id.</summary>
        public CutOperationId Operation { get; }

        /// <summary>The fragment both sides still resolve to: the cut's source, which is not split (DESIGN 7.1.1).</summary>
        public LogicalFragmentId Source { get; }

        public PhysicsOwnerSide Positive { get; private set; }

        public PhysicsOwnerSide Negative { get; private set; }

        /// <summary>The shape each side shares with the source. Neither is the source's own.</summary>
        public PhysicsOwnerShape PositiveShape { get; private set; }

        public PhysicsOwnerShape NegativeShape { get; private set; }

        /// <summary>The sibling separation constraint (DESIGN 7.1.1), on one of the two actors.</summary>
        public ConfigurableJoint Separation { get; private set; }

        /// <summary>
        /// How the source lineage's display geometry sits in an owner's own coordinates, carried from the source
        /// because both sides are published at the very placement it had. None for a lineage whose display is
        /// arranged some other way, which is a statement and not a gap.
        /// </summary>
        public Matrix4x4? GeometryLocalToOwner { get; }

        /// <summary>Whether this pair has been ended: out of the scene, destroyed, and its holds given back.</summary>
        public bool IsEnded { get; private set; }

        /// <summary>
        /// Whether the shapes it holds are the final ones a handoff has begun switching to, rather than the temporary
        /// ones it was published with. It says who gives them back: while this is false the pair does, and once the
        /// actors have been handed over the children's owners do.
        /// </summary>
        public bool HoldsFinalShapes { get; private set; }

        public PhysicsOwnerSide Side(bool positive)
        {
            return positive ? Positive : Negative;
        }

        /// <summary>Whether that side is in the scene now.</summary>
        public bool IsStanding(bool positive)
        {
            PhysicsOwnerSide side = Side(positive);
            return !IsEnded && side?.Root != null && side.Root.activeInHierarchy;
        }

        /// <summary>
        /// Where one side's display geometry stands now: that side's world transform, read at the moment it is
        /// needed, with the correspondence the source had. False for a pair that has no correspondence, and for one
        /// whose side has left the scene — nothing of where it used to be is handed back.
        /// </summary>
        public bool TryReadGeometryLocalToWorld(bool positive, out Matrix4x4 geometryLocalToWorld)
        {
            PhysicsOwnerSide side = Side(positive);
            if (IsEnded || side?.Root == null || GeometryLocalToOwner == null)
            {
                geometryLocalToWorld = default;
                return false;
            }

            geometryLocalToWorld = side.Root.transform.localToWorldMatrix * GeometryLocalToOwner.Value;
            return true;
        }

        /// <summary>
        /// The shapes the two actors are being switched to are this pair's from here on, and the temporary ones it was
        /// published with go back at the same moment -- their holds were this pair's.
        /// <para>
        /// **It is called as the switch begins** (DESIGN 7.2), before the actors stand on anything of the new shapes.
        /// Until the actors are handed over, this pair is what holds them, so an exception in the middle of the switch
        /// leaves them where the ordinary ending of a published pair
        /// (<see cref="PhysicsOwnerRegistry.EndProvisional"/>) gives them back. Nothing else can reach them: they are
        /// not the correspondence's yet, and a caller's local variable is not something an ending can find.
        /// </para>
        /// </summary>
        internal void TakeFinalShapes(PhysicsOwnerShape positive, PhysicsOwnerShape negative)
        {
            if (IsEnded)
            {
                throw new InvalidOperationException("this pair has ended");
            }

            PhysicsOwnerShape wasPositive = PositiveShape;
            PhysicsOwnerShape wasNegative = NegativeShape;
            PositiveShape = positive;
            NegativeShape = negative;
            HoldsFinalShapes = true;
            wasPositive?.Dispose();
            wasNegative?.Dispose();
        }

        /// <summary>
        /// Gives the two actors up to whoever takes them over, **without ending them**: the objects stay in the scene
        /// and stay as they are, and the constraint is destroyed because it belongs to this pair and not to them. The
        /// shapes go with the actors, and **nothing of them is given back here**. This pair stops naming any of it.
        /// <para>
        /// It is the handoff of DESIGN 7.2, where the same actors are given the final shape: the actor is the
        /// authority and is not rebuilt. **It is not <see cref="End"/>**, which destroys them — the two are told apart
        /// here so that taking a pair out of the correspondence cannot silently destroy actors somebody else now owns.
        /// </para>
        /// <para>
        /// The shapes go with the actors when they are the final ones (<see cref="TakeFinalShapes"/>): the owners the
        /// caller registers hold them, so giving them back here would take away what the children are standing on.
        /// A pair that never got that far gives its own temporary shapes back, as it does when it ends.
        /// </para>
        /// </summary>
        internal void HandOver(out PhysicsOwnerSide positive, out PhysicsOwnerSide negative)
        {
            positive = Positive;
            negative = Negative;
            if (IsEnded)
            {
                return;
            }

            IsEnded = true;
            if (Separation != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(Separation);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(Separation);
                }
            }

            Separation = null;
            Positive = null;
            Negative = null;
            if (!HoldsFinalShapes)
            {
                PositiveShape?.Dispose();
                NegativeShape?.Dispose();
            }

            PositiveShape = null;
            NegativeShape = null;
        }

        /// <summary>
        /// Ends this pair: both sides leave the physics scene at once, then the objects are destroyed — the
        /// constraint with the object it is on — and the two shapes are given up, which hands their mesh holds back.
        /// Once, whatever happens afterwards.
        /// <para>
        /// The order is the one the rest of the code uses: **out of the scene first, destroyed after**. What the
        /// meshes themselves are is not decided here: a mesh goes back when the last holder lets go, and the source's
        /// own owner is one of those holders until it is retired in its turn. Ending this pair does not retire the
        /// source and does not touch the ledger.
        /// </para>
        /// <para>
        /// It is the correspondence's to call, once it has forgotten this pair: see
        /// <see cref="PhysicsOwnerRegistry.EndProvisional"/>.
        /// </para>
        /// </summary>
        internal void End()
        {
            if (IsEnded)
            {
                return;
            }

            IsEnded = true;
            if (Positive?.Root != null)
            {
                Positive.Root.SetActive(false);
            }

            if (Negative?.Root != null)
            {
                Negative.Root.SetActive(false);
            }

            Separation = null;
            PhysicsOwnerBuilder.DestroyObject(Positive?.Root);
            PhysicsOwnerBuilder.DestroyObject(Negative?.Root);
            Positive = null;
            Negative = null;
            PositiveShape?.Dispose();
            NegativeShape?.Dispose();
            PositiveShape = null;
            NegativeShape = null;
        }
    }
}
