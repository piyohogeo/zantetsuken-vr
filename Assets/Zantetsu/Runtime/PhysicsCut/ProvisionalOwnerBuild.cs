using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What one unpublished Provisional build is asked for. Everything is the caller's and is only read.
    /// </summary>
    public struct ProvisionalOwnerBuildInput
    {
        /// <summary>The source owner's current shape. It is read, never changed, and it keeps its own meshes.</summary>
        public PhysicsOwnerShape sourceShape;

        /// <summary>
        /// The 7.6 disposition of each convex of that shape, by convex index. A convex the plane crosses
        /// (<see cref="ConvexSide.Split"/>) goes to **both** sides, uncut; one with support on one side only goes
        /// there; one with neither goes to the positive side. Nothing is classified again here.
        /// </summary>
        public IReadOnlyList<ConvexSide> sides;

        /// <summary>
        /// The adopted plane, in the same numerical local frame as the shape's B-rep — the frame the 7.6 distances
        /// were taken in. It is carried into the owner's frame here by the shape's own mapping.
        /// </summary>
        public float4 planeLocal;

        /// <summary>Where the source owner is now. Both sides are placed here (DESIGN 7.1.1).</summary>
        public PhysicsOwnerPlacement placement;

        /// <summary>The source's motion now, with the render anchor the caller names (DESIGN 7.2).</summary>
        public PhysicsOwnerMotion sourceMotion;

        /// <summary>
        /// The ledger's settled distribution of the source's anchors across this cut. It is used as it stands, and
        /// is the same distribution Final will use (DESIGN 7.1.1).
        /// </summary>
        public AnchorDistributionResult anchors;

        /// <summary>The parent mass snapshotted at admission, which the two temporary masses are taken from.</summary>
        public double parentMass;

        /// <summary>
        /// The source actor's inertia tensor now. It is the fallback of DESIGN 7.2: where a box inertia cannot be
        /// made finite, this is scaled by the side's temporary mass over the parent mass.
        /// </summary>
        public float3 sourceInertia;

        /// <summary>
        /// The orientation <see cref="sourceInertia"/> is expressed in — the source actor's
        /// <c>inertiaTensorRotation</c>. Unity names an inertia by the pair, so the fallback carries this with the
        /// three numbers: a source whose principal axes are not its actor axes keeps them.
        /// </summary>
        public quaternion sourceInertiaRotation;

        /// <summary>
        /// The cooking profile the source's collider meshes were baked with. The colliders here are given the same
        /// one **before** their mesh, which is the order that leaves the existing bake alone (DESIGN 7.3, 7.1.1).
        /// How often PhysX cooks underneath is not something this says anything about.
        /// </summary>
        public MeshColliderCookingOptions cooking;

        public string name;
    }

    /// <summary>
    /// The two unpublished Provisional actors, the shapes they share with the source, and the constraint between
    /// them. This owns what the build made: the two objects, the two shapes, and through those shapes the holds on
    /// whoever owns the collider meshes. Disposing gives all of it back, once.
    /// <para>
    /// The existing <see cref="PhysicsOwnerCandidate"/> of a final split owns its objects but not its shapes, because
    /// there the shapes are made by the publication. Here the shapes are made by the build, so the build owns them
    /// and there is one place that gives everything back.
    /// </para>
    /// </summary>
    public sealed class ProvisionalOwnerCandidate : IDisposable
    {
        internal ProvisionalOwnerCandidate(
            PhysicsOwnerSide positive, PhysicsOwnerSide negative,
            PhysicsOwnerShape positiveShape, PhysicsOwnerShape negativeShape,
            ConfigurableJoint separation)
        {
            Positive = positive;
            Negative = negative;
            PositiveShape = positiveShape;
            NegativeShape = negativeShape;
            Separation = separation;
        }

        public PhysicsOwnerSide Positive { get; private set; }

        public PhysicsOwnerSide Negative { get; private set; }

        /// <summary>The shape each side shares with the source. Neither is the source's own.</summary>
        public PhysicsOwnerShape PositiveShape { get; private set; }

        public PhysicsOwnerShape NegativeShape { get; private set; }

        /// <summary>
        /// The sibling separation constraint of DESIGN 7.1.1, on one of the two actors. It is configured here and
        /// enters no physics scene while both actors are inactive.
        /// </summary>
        public ConfigurableJoint Separation { get; private set; }

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// The two actors, their shapes and the constraint have been handed over to a
        /// <see cref="ProvisionalOwnerPair"/> and are not this one's any more.
        /// </summary>
        public bool IsDetached { get; private set; }

        public PhysicsOwnerSide Side(bool positive)
        {
            return positive ? Positive : Negative;
        }

        /// <summary>
        /// Gives everything this built up to whoever published it: the two actors, the constraint on one of them and
        /// the two shapes with the holds they took. This candidate stops naming them, so disposing it afterwards
        /// destroys nothing and gives nothing back twice. From here the pair is what ends them.
        /// </summary>
        internal void Detach()
        {
            IsDetached = true;
            Positive = null;
            Negative = null;
            PositiveShape = null;
            NegativeShape = null;
            Separation = null;
        }

        /// <summary>
        /// Gives back everything this build made: the two objects, with the constraint on one of them, and the two
        /// shapes with the holds they took. The source's meshes are not destroyed — they are the source's, and the
        /// holds are what kept them from going away while these shapes named them.
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
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

    /// <summary>
    /// Builds the unpublished Provisional pair of one accepted cut (DESIGN 7.1.1): two actors at the source's
    /// placement, sharing the source's already cooked convexes as the 7.6 classification allocates them, with the
    /// sibling separation constraint between them, all of it inactive.
    /// <para>
    /// **What it does not do.** It makes no cut, copies no mesh and calls no bake. It does not touch the source, the
    /// ledger, the correspondence or the display. It puts nothing into the physics scene, applies no separation
    /// impulse, suppresses no collision, and publishes nothing: a caller that does not publish this simply disposes
    /// it, and everything the build took goes back. The lease of DESIGN 7.1.1 that has to outlive the physics steps
    /// a published pair needs is **not** what the holds here are — these keep the meshes while these shapes name
    /// them, which is all an unpublished pair needs.
    /// </para>
    /// <para>
    /// **The masses here are temporary** (DESIGN 7.2): an approximation from a conservative box and this cut's
    /// plane, for a short-lived solver. They are not the final authority, and the final split's builder, which takes
    /// the kernel's own mass properties, is not used or changed here.
    /// </para>
    /// <para>
    /// **How it ends** is said in <see cref="PhysicsOwnerBuildOutcome"/>, the same reasons a final split ends by: no
    /// new reason is introduced for this path, and nothing of the final path's behaviour changes. A build that throws
    /// part way lets the exception out, having given back everything it had made.
    /// </para>
    /// </summary>
    public static class ProvisionalOwnerBuilder
    {
        /// <summary>
        /// Called with each side once it is built, for the test that needs one to fail there. A build that throws
        /// part way is the one case this cannot be shown without a seam, as it is on the final path.
        /// </summary>
        internal static Action<bool> sideBuiltHook;

        public static bool TryBuild(
            in ProvisionalOwnerBuildInput input,
            out ProvisionalOwnerCandidate candidate,
            out PhysicsOwnerBuildOutcome outcome)
        {
            candidate = null;

            // A shape that has been given back has no bank to read and no meshes to name, so it is refused before
            // anything reads it rather than scanned.
            if (input.sourceShape == null || input.sourceShape.IsFreed || input.sides == null
                || input.sides.Count != input.sourceShape.ConvexCount
                || !(input.parentMass > 0.0) || !IsFinite(input.parentMass)
                || !math.all(math.isfinite(input.planeLocal))
                || math.lengthsq(input.planeLocal.xyz) <= 0f
                || !math.all(math.isfinite(input.sourceInertia))
                || !math.all(math.isfinite(input.sourceInertiaRotation.value))
                || math.lengthsq(input.sourceInertiaRotation.value) <= 0f
                || !input.placement.IsFinite || !input.sourceMotion.IsFinite)
            {
                outcome = PhysicsOwnerBuildOutcome.InvalidInput;
                return false;
            }

            // 7.6, as the caller settled it: a crossed convex is on both sides, uncut. The four dispositions 7.6
            // defines are named one by one; anything else is a disposition this call was not given, not a default.
            int positiveCount = 0;
            int negativeCount = 0;
            for (int c = 0; c < input.sides.Count; c++)
            {
                switch (input.sides[c])
                {
                    case ConvexSide.Split:
                        positiveCount++;
                        negativeCount++;
                        break;
                    case ConvexSide.Negative:
                        negativeCount++;
                        break;
                    case ConvexSide.Positive:
                    case ConvexSide.NearPlaneToPositive:
                        // Neither support goes to the positive side (DESIGN 7.6).
                        positiveCount++;
                        break;
                    default:
                        outcome = PhysicsOwnerBuildOutcome.InvalidInput;
                        return false;
                }
            }

            if (positiveCount == 0 || negativeCount == 0)
            {
                outcome = PhysicsOwnerBuildOutcome.SideEmpty;
                return false;
            }

            // These become the shapes' index views: no temporary List or second index-array copy is needed.
            var positiveConvexes = new int[positiveCount];
            var negativeConvexes = new int[negativeCount];
            int positiveAt = 0;
            int negativeAt = 0;

            // Every convex a side names must still have the cooked mesh its collider will use. A destroyed or absent
            // one is found here, before any hold is taken, rather than becoming a collider with no shape.
            for (int c = 0; c < input.sides.Count; c++)
            {
                if (input.sourceShape.MeshOf(c) == null)
                {
                    outcome = PhysicsOwnerBuildOutcome.ShapeMissing;
                    return false;
                }

                ConvexSide side = input.sides[c];
                if (side != ConvexSide.Negative)
                {
                    positiveConvexes[positiveAt++] = c;
                }

                if (side == ConvexSide.Split || side == ConvexSide.Negative)
                {
                    negativeConvexes[negativeAt++] = c;
                }
            }

            // The shape's own frame has to be rigid, as it does for a final split: the same check, not a second one.
            if (!PhysicsOwnerBuilder.TryRigid(
                    input.sourceShape.LocalToOwner, out quaternion localRotation, out float3 localOffset))
            {
                outcome = PhysicsOwnerBuildOutcome.FrameNotRigid;
                return false;
            }

            if (!ProvisionalBoxMass.TryDivide(
                    input.sourceShape, input.planeLocal, input.parentMass,
                    input.sourceInertia, input.sourceInertiaRotation,
                    out ProvisionalBoxMass.Side positiveMass, out ProvisionalBoxMass.Side negativeMass,
                    out float3 planeNormalOwner))
            {
                outcome = PhysicsOwnerBuildOutcome.MassNotUsable;
                return false;
            }

            PhysicsOwnerShape positiveShape = null;
            PhysicsOwnerShape negativeShape = null;
            PhysicsOwnerSide positive = null;
            PhysicsOwnerSide negative = null;
            try
            {
                positiveShape = PhysicsOwnerShape.ProvisionalSideFromOwnedIndices(input.sourceShape, positiveConvexes);
                negativeShape = PhysicsOwnerShape.ProvisionalSideFromOwnedIndices(input.sourceShape, negativeConvexes);
                positive = BuildSide(in input, true, positiveShape, in positiveMass, localRotation, localOffset);
                negative = BuildSide(in input, false, negativeShape, in negativeMass, localRotation, localOffset);

                // The motion of the first split (DESIGN 7.2), the same formula a direct final split uses. No
                // separation impulse is applied here: that belongs to the publication.
                positive.Reposition(input.placement, in input.sourceMotion, planeNormalOwner, 0f);
                negative.Reposition(input.placement, in input.sourceMotion, planeNormalOwner, 0f);

                ConfigurableJoint separation = ProvisionalSeparation.Configure(positive, negative, planeNormalOwner);
                candidate = new ProvisionalOwnerCandidate(positive, negative, positiveShape, negativeShape, separation);
            }
            catch
            {
                // Nothing of a half-built pair is left behind: the objects this call made and the holds its shapes
                // took go back here, and the source is untouched either way.
                PhysicsOwnerBuilder.DestroyObject(positive?.Root);
                PhysicsOwnerBuilder.DestroyObject(negative?.Root);
                positiveShape?.Dispose();
                negativeShape?.Dispose();
                throw;
            }

            outcome = PhysicsOwnerBuildOutcome.Ok;
            return true;
        }

        private static PhysicsOwnerSide BuildSide(
            in ProvisionalOwnerBuildInput input, bool positive, PhysicsOwnerShape shape,
            in ProvisionalBoxMass.Side mass, quaternion localRotation, float3 localOffset)
        {
            string name = string.IsNullOrEmpty(input.name) ? "Provisional Owner" : input.name;
            var root = new GameObject(positive ? name + " +" : name + " -");
            try
            {
                // Inactive before anything physical is on it, so nothing of this build enters the physics scene.
                root.SetActive(false);

                var shapeFrame = new GameObject("Shape Frame");
                Transform frame = shapeFrame.transform;
                frame.SetParent(root.transform, false);
                frame.SetLocalPositionAndRotation(localOffset, localRotation);

                var body = root.AddComponent<Rigidbody>();
                var side = new PhysicsOwnerSide(positive, root, shapeFrame, body);
                for (int i = 0; i < shape.ConvexCount; i++)
                {
                    var collider = shapeFrame.AddComponent<MeshCollider>();

                    // The profile before the mesh, and the mesh the source already had: a collider given its mesh
                    // first would be cooked with the wrong options (DESIGN 7.1.1).
                    collider.cookingOptions = input.cooking;
                    collider.convex = true;
                    collider.sharedMesh = shape.MeshOf(i);
                    side.Add(collider);
                }

                side.ProducedColliderCount = 0;
                side.Mass = mass.mass;
                side.CenterOfMass = mass.centerOfMass;
                side.InertiaTensor = mass.inertia;
                side.InertiaRotation = mass.inertiaRotation;
                side.FixedByAnchors = positive ? input.anchors.IsPositiveFixed : input.anchors.IsNegativeFixed;
                sideBuiltHook?.Invoke(positive);
                return side;
            }
            catch
            {
                PhysicsOwnerBuilder.DestroyObject(root);
                throw;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
