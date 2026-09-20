using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How one attempt to build the two Final Physics Owners of a cut ended.</summary>
    public enum PhysicsOwnerBuildOutcome
    {
        /// <summary>No attempt was made. Nothing is ever handed over with this.</summary>
        NotBuilt = 0,

        /// <summary>Both sides were built, unpublished.</summary>
        Ok = 1,

        /// <summary>
        /// The call was refused before anything was built: a missing product, a placement or motion that is not
        /// finite, a mass snapshot that is not a positive finite number, or an anchor distribution the ledger
        /// refused.
        /// </summary>
        InvalidInput = 2,

        /// <summary>A side has no convex at all, so there is no owner for it to be. Both sides or neither.</summary>
        SideEmpty = 3,

        /// <summary>
        /// A part has no shape to use: a produced convex without its baked mesh, or an inherited one whose existing
        /// cooked mesh was not given.
        /// </summary>
        ShapeMissing = 4,

        /// <summary>
        /// The transform from the numerical local frame to the owner's is not a rigid one. A frame correspondence
        /// that scales or shears would make the colliders and the mass properties describe different shapes, so it is
        /// refused rather than applied to one of them.
        /// </summary>
        FrameNotRigid = 5,

        /// <summary>
        /// The mass properties are not ones the solver can be given (DESIGN 7.2): a side's mass is not finite and
        /// positive, the two do not add up to the parent's, a centre of mass is not finite, or the inertia has no
        /// usable principal form. Nothing is substituted for them and nothing is moved between the sides.
        /// </summary>
        MassNotUsable = 6,

        /// <summary>
        /// Building threw. Everything this call had made is destroyed; the source and the borrowed shapes are
        /// untouched.
        /// </summary>
        BuildFailed = 7,
    }

    /// <summary>
    /// Where an owner is. This is a rigid placement by construction: the owner frame in the world, which both sides
    /// start at, because a first split keeps the source's pose (DESIGN 7.2). The separation offset is not applied
    /// here.
    /// <para>
    /// The rotation is normalized once, here, and that one value is what the owner's transform is given and what
    /// carries the centre of mass and the velocity into the world. A quaternion of another length names the same
    /// orientation but would scale those points, so it is made a rotation before anything uses it rather than at each
    /// use. One that cannot be normalized stops being finite and is refused by <see cref="IsFinite"/>, as before.
    /// </para>
    /// </summary>
    public readonly struct PhysicsOwnerPlacement
    {
        public PhysicsOwnerPlacement(float3 position, quaternion rotation)
        {
            this.position = position;
            this.rotation = math.normalize(rotation);
        }

        public readonly float3 position;

        /// <summary>The owner's orientation, as a rotation: normalized when this placement was made.</summary>
        public readonly quaternion rotation;

        public static PhysicsOwnerPlacement Identity => new PhysicsOwnerPlacement(float3.zero, quaternion.identity);

        public bool IsFinite => math.all(math.isfinite(position)) && math.all(math.isfinite(rotation.value))
                                && math.lengthsq(rotation.value) > 1e-12f;

        /// <summary>A point of the owner frame, in the world.</summary>
        public float3 ToWorld(float3 local)
        {
            return position + math.mul(rotation, local);
        }
    }

    /// <summary>
    /// The motion the two sides inherit, all in world coordinates: the source actor's centre of mass and its linear
    /// and angular velocity at the split, and the render anchor that is the authoritative point of the inheritance
    /// (DESIGN 7.2).
    /// <para>
    /// This is the **first split**: Source to Provisional, or Source straight to Final where Provisional is skipped.
    /// It is not the Provisional to Final handoff, which keeps the existing actor's pose, centre-of-mass linear
    /// velocity and angular velocity and replaces only its shape and mass properties. This builder makes the
    /// candidates of a direct Final split, so what it needs is the source's motion at the split.
    /// </para>
    /// </summary>
    public readonly struct PhysicsOwnerMotion
    {
        public PhysicsOwnerMotion(float3 centerOfMass, float3 linearVelocity, float3 angularVelocity, float3 renderAnchor)
        {
            this.centerOfMass = centerOfMass;
            this.linearVelocity = linearVelocity;
            this.angularVelocity = angularVelocity;
            this.renderAnchor = renderAnchor;
        }

        /// <summary>The source actor's centre of mass, in the world; the point its linear velocity belongs to.</summary>
        public readonly float3 centerOfMass;

        /// <summary>The source actor's linear velocity at its centre of mass.</summary>
        public readonly float3 linearVelocity;

        /// <summary>The source actor's angular velocity.</summary>
        public readonly float3 angularVelocity;

        /// <summary>The FragmentRenderAnchor: the point whose velocity is carried across the split unchanged.</summary>
        public readonly float3 renderAnchor;

        public static PhysicsOwnerMotion AtRest => default;

        public bool IsFinite => math.all(math.isfinite(centerOfMass)) && math.all(math.isfinite(linearVelocity))
                                && math.all(math.isfinite(angularVelocity)) && math.all(math.isfinite(renderAnchor));
    }

    /// <summary>What one owner build was asked for.</summary>
    public struct PhysicsOwnerBuildInput
    {
        /// <summary>
        /// The finished cut. It stays the caller's, and must outlive the owners: their colliders use the meshes it
        /// owns.
        /// </summary>
        public PhysicsCutProducts products;

        /// <summary>Where the owner frame is. Both sides are placed here.</summary>
        public PhysicsOwnerPlacement placement;

        /// <summary>The source's motion at the split, in the world.</summary>
        public PhysicsOwnerMotion sourceMotion;

        /// <summary>
        /// The ledger's own distribution of the source's anchors across this cut (DESIGN 7.1). It is used as it
        /// stands: a side that received an anchor is fixed, the other is dynamic. Nothing is classified again here.
        /// </summary>
        public AnchorDistributionResult anchors;

        /// <summary>
        /// The parent Rigidbody mass snapshotted when the cut was accepted, which DESIGN 7.2 makes the authoritative
        /// total. The two sides are required to add up to it.
        /// </summary>
        public double parentMass;

        /// <summary>
        /// The existing cooked collider mesh of each input convex, by input convex index. Only the entries an
        /// inherited part names are read, and those meshes are borrowed: they are neither copied, re-baked nor
        /// destroyed here, and must outlive the owners.
        /// </summary>
        public IReadOnlyList<Mesh> inheritedMeshes;

        /// <summary>What to call the two objects. Optional.</summary>
        public string name;
    }

    /// <summary>
    /// One side's Final Physics Owner, built and not published: one Rigidbody with that side's convexes as its
    /// collider group, and the values it was given.
    /// <para>
    /// **The values are kept here, and this is where they come from.** A Rigidbody on an inactive object has no body
    /// in the physics scene to hold them: its mass survives, but the centre of mass, the inertia and the velocities
    /// do not, and reading them back gives the automatic ones. So what this build decided is recorded here and
    /// written onto the body by <see cref="ApplyToBody"/>, which the build calls once and the step that publishes the
    /// pair calls again with the owner in the scene. Nothing here reads the body to find out what it decided.
    /// </para>
    /// </summary>
    public sealed class PhysicsOwnerSide
    {
        private readonly List<MeshCollider> _colliders = new List<MeshCollider>(4);

        internal PhysicsOwnerSide(bool positive, GameObject root, GameObject shapeFrame, Rigidbody body)
        {
            this.positive = positive;
            Root = root;
            ShapeFrame = shapeFrame;
            Body = body;
        }

        /// <summary>Which side of the adopted plane this is.</summary>
        public readonly bool positive;

        /// <summary>The object the Rigidbody is on, placed at the owner placement and inactive.</summary>
        public GameObject Root { get; }

        /// <summary>
        /// The child that carries the numerical local frame. The colliders are on it, because their meshes are in
        /// that frame and are not rewritten: this is where the B-rep, the colliders and the mass properties are made
        /// to correspond.
        /// </summary>
        public GameObject ShapeFrame { get; }

        public Rigidbody Body { get; }

        /// <summary>The colliders of this side, produced and inherited alike, all under <see cref="Body"/>.</summary>
        public IReadOnlyList<MeshCollider> Colliders => _colliders;

        /// <summary>How many of the colliders use a mesh this cut produced. The rest borrow the input's.</summary>
        public int ProducedColliderCount { get; internal set; }

        /// <summary>This side received at least one anchor, so it is fixed (DESIGN 7.1).</summary>
        public bool FixedByAnchors { get; internal set; }

        /// <summary>The side's mass, as the kernel computed it.</summary>
        public double Mass { get; internal set; }

        /// <summary>The centre of mass, in the owner frame.</summary>
        public float3 CenterOfMass { get; internal set; }

        /// <summary>The principal moments, about that centre of mass.</summary>
        public float3 InertiaTensor { get; internal set; }

        /// <summary>The rotation of the principal axes, in the owner frame.</summary>
        public quaternion InertiaRotation { get; internal set; }

        /// <summary>
        /// The linear velocity of this side's centre of mass, from the first-split inheritance (DESIGN 7.2). Zero on
        /// a fixed side, which takes no offset and no impulse.
        /// </summary>
        public float3 LinearVelocity { get; internal set; }

        /// <summary>The angular velocity, which is the source's on a free side and zero on a fixed one.</summary>
        public float3 AngularVelocity { get; internal set; }

        /// <summary>
        /// Writes the mass properties and the first-split motion onto the body. It is called when the side is built
        /// and must be called again at publication, when the owner is in the scene: an inactive body keeps none of
        /// this. It changes nothing about the owner's place in the scene, and publishes nothing by itself.
        /// </summary>
        public void ApplyToBody()
        {
            if (Body == null)
            {
                return;
            }

            Body.automaticCenterOfMass = false;
            Body.automaticInertiaTensor = false;
            Body.mass = (float)Mass;
            Body.centerOfMass = CenterOfMass;
            Body.inertiaTensor = InertiaTensor;
            Body.inertiaTensorRotation = InertiaRotation;
            Body.isKinematic = FixedByAnchors;
            if (FixedByAnchors)
            {
                // A fixed side takes no offset and no impulse (DESIGN 7.2), and a kinematic body has no velocity to
                // be given.
                return;
            }

            Body.linearVelocity = LinearVelocity;
            Body.angularVelocity = AngularVelocity;
        }

        internal void Add(MeshCollider collider)
        {
            _colliders.Add(collider);
        }
    }

    /// <summary>
    /// The two Final Physics Owners of one cut, built and unpublished (DESIGN 7.1.2, 7.2).
    /// <para>
    /// **Nothing here is in the physics scene.** Both objects are inactive, so no actor, shape or query sees them,
    /// and there is no half of this: a build either hands over both sides or nothing at all. What publishes them, in
    /// one step with the logical publication, is not written yet; neither is the separation impulse, nor any change
    /// to the source.
    /// </para>
    /// <para>
    /// **What it owns.** The two objects it made, with their Rigidbodies and colliders. It owns no mesh: the produced
    /// ones belong to the <see cref="PhysicsCutProducts"/> and the inherited ones to the caller, and both must
    /// outlive these owners because the colliders use them. Disposing this destroys the two objects and leaves every
    /// mesh alone, so the order at the end is these first and the products after.
    /// </para>
    /// </summary>
    public sealed class PhysicsOwnerCandidate : IDisposable
    {
        internal PhysicsOwnerCandidate(PhysicsOwnerSide positive, PhysicsOwnerSide negative)
        {
            Positive = positive;
            Negative = negative;
        }

        public PhysicsOwnerSide Positive { get; private set; }

        public PhysicsOwnerSide Negative { get; private set; }

        public bool IsDisposed { get; private set; }

        public PhysicsOwnerSide Side(bool positive)
        {
            return positive ? Positive : Negative;
        }

        /// <summary>Destroys the two owners. The meshes they used are not this call's and are left as they were.</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            PhysicsOwnerBuilder.DestroySide(Positive);
            PhysicsOwnerBuilder.DestroySide(Negative);
            Positive = null;
            Negative = null;
        }
    }

    /// <summary>
    /// Builds the positive and negative Final Physics Owners of a finished cut, without publishing them
    /// (DESIGN 7.1.2, 7.2).
    /// <para>
    /// One owner per side: that side's convexes become a collider group under one Rigidbody, whatever the cut made of
    /// them. A convex the plane split uses the mesh this cut produced and cooked; one it did not split uses the
    /// existing cooked mesh of the input convex it inherited, borrowed as it is — not copied and not baked again.
    /// Islands are not separated and no convex gets a body of its own (DESIGN 7.6).
    /// </para>
    /// <para>
    /// The frames are made to correspond rather than assumed identical: the owner is placed where the caller says,
    /// the colliders sit in the numerical local frame under it, and the centre of mass and inertia are carried into
    /// the owner frame with the same transform. The masses are the kernel's, checked against the parent snapshot; the
    /// fixity is the ledger's anchor distribution, used and not recomputed; the velocity is the first-split
    /// inheritance about the render anchor.
    /// </para>
    /// <para>
    /// **These are the candidates of a direct Final split**, the case where Provisional is skipped (DESIGN 7.1.1).
    /// This is not the builder of a Provisional to Final handoff, which keeps an existing actor and replaces only
    /// what is on it, and it is not a change to the rule that ordinary cuts go through Provisional.
    /// </para>
    /// <para>
    /// **Failures are returned, not acted on.** A build that cannot be completed gives back everything it had made
    /// and says why. It does not abort a transaction, retire a source, or touch the products and the borrowed
    /// meshes. Nothing looks at the cooked hulls and nothing watches the owners afterwards.
    /// </para>
    /// </summary>
    public static class PhysicsOwnerBuilder
    {
        /// <summary>How far the two sides' masses may be from the parent snapshot, relative to it.</summary>
        private const double MassTolerance = 1e-6;

        /// <summary>How far the frame transform may be from a rigid one before it is refused.</summary>
        private const float RigidTolerance = 1e-4f;

        private static readonly bool[] Sides = { true, false };

        /// <summary>
        /// Called with each side once it is built, for the tests that need one to fail there. A build that throws
        /// part way is the one case this cannot be shown without a seam, and there is nothing else in the product
        /// that would make the second side fail.
        /// </summary>
        internal static Action<bool> sideBuiltHook;

        public static bool TryBuild(
            in PhysicsOwnerBuildInput input, out PhysicsOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome)
        {
            candidate = null;
            PhysicsCutProducts products = input.products;
            if (products == null || input.anchors.status != AnchorDistributionStatus.Ok
                || !(input.parentMass > 0.0) || !math.isfinite(input.parentMass)
                || !input.placement.IsFinite || !input.sourceMotion.IsFinite)
            {
                outcome = PhysicsOwnerBuildOutcome.InvalidInput;
                return false;
            }

            if (products.PartCount(true) == 0 || products.PartCount(false) == 0)
            {
                // A side with no convex is not an owner with an empty shape: there is no owner for it. The caller is
                // told, and decides what that means for its transaction.
                outcome = PhysicsOwnerBuildOutcome.SideEmpty;
                return false;
            }

            if (!TryRigid(products.LocalToOwner, out quaternion localRotation, out float3 localOffset))
            {
                outcome = PhysicsOwnerBuildOutcome.FrameNotRigid;
                return false;
            }

            if (!ShapesArePresent(products, input.inheritedMeshes))
            {
                outcome = PhysicsOwnerBuildOutcome.ShapeMissing;
                return false;
            }

            ConvexCutOwnerResult result = products.Result;
            if (!TryMass(in result, input.parentMass, true, localRotation, localOffset, out SideMass positiveMass)
                || !TryMass(in result, input.parentMass, false, localRotation, localOffset, out SideMass negativeMass))
            {
                outcome = PhysicsOwnerBuildOutcome.MassNotUsable;
                return false;
            }

            // Everything that can be judged has been judged; only now is anything made, so an ordinary refusal leaves
            // nothing behind at all.
            PhysicsOwnerSide positive = null;
            PhysicsOwnerSide negative = null;
            try
            {
                positive = BuildSide(in input, true, in positiveMass, localRotation, localOffset);
                negative = BuildSide(in input, false, in negativeMass, localRotation, localOffset);
            }
            catch (Exception)
            {
                // One side alone is never handed over, and nothing of a half-built pair is kept.
                DestroySide(positive);
                DestroySide(negative);
                outcome = PhysicsOwnerBuildOutcome.BuildFailed;
                return false;
            }

            candidate = new PhysicsOwnerCandidate(positive, negative);
            outcome = PhysicsOwnerBuildOutcome.Ok;
            return true;
        }

        /// <summary>The mass properties of one side, already carried into the owner frame.</summary>
        private readonly struct SideMass
        {
            internal SideMass(double mass, float3 centerOfMass, float3 inertia, quaternion inertiaRotation)
            {
                this.mass = mass;
                this.centerOfMass = centerOfMass;
                this.inertia = inertia;
                this.inertiaRotation = inertiaRotation;
            }

            internal readonly double mass;
            internal readonly float3 centerOfMass;
            internal readonly float3 inertia;
            internal readonly quaternion inertiaRotation;
        }

        private static bool TryMass(
            in ConvexCutOwnerResult result,
            double parentMass,
            bool positive,
            quaternion localRotation,
            float3 localOffset,
            out SideMass side)
        {
            side = default;
            double mass = positive ? result.positiveMass : result.negativeMass;
            double other = positive ? result.negativeMass : result.positiveMass;
            if (!(mass > 0.0) || !math.isfinite(mass) || !(other > 0.0) || !math.isfinite(other))
            {
                return false;
            }

            // The parent's snapshot is the authoritative total (DESIGN 7.2). Nothing is scaled to make this true.
            if (math.abs((mass + other) - parentMass) > parentMass * MassTolerance)
            {
                return false;
            }

            double3 centerLocal = positive ? result.positiveCenterOfMass : result.negativeCenterOfMass;
            if (!math.all(math.isfinite(centerLocal)))
            {
                return false;
            }

            SymmetricMatrix3 inertiaLocal = positive ? result.positiveInertia : result.negativeInertia;
            if (!PrincipalInertia.TryDiagonalize(in inertiaLocal, out double3 moments, out quaternion principal))
            {
                return false;
            }

            // A moment the solver cannot be given is a failure of this side, not something to raise to a minimum.
            var inertia = (float3)moments;
            if (!math.all(math.isfinite(inertia)) || math.any(inertia <= 0f))
            {
                return false;
            }

            // The numbers are in the numerical local frame; the body wants them in the owner's. The same transform
            // that places the colliders carries them, so the shape and the mass properties stay the same thing.
            float3 center = localOffset + math.mul(localRotation, (float3)centerLocal);
            quaternion rotation = math.normalize(math.mul(localRotation, principal));
            var massFloat = (float)mass;
            if (!math.all(math.isfinite(center)) || !math.all(math.isfinite(rotation.value)) || !(massFloat > 0f)
                || !math.isfinite(massFloat))
            {
                return false;
            }

            side = new SideMass(mass, center, inertia, rotation);
            return true;
        }

        private static PhysicsOwnerSide BuildSide(
            in PhysicsOwnerBuildInput input, bool positive, in SideMass mass, quaternion localRotation, float3 localOffset)
        {
            PhysicsCutProducts products = input.products;
            string name = string.IsNullOrEmpty(input.name) ? "Physics Owner" : input.name;
            var root = new GameObject(positive ? name + " +" : name + " -");
            PhysicsOwnerSide side;
            try
            {
                // Inactive before anything physical is on it: a Rigidbody or a collider added to an inactive object
                // does not enter the physics scene, which is what keeps this build unpublished.
                root.SetActive(false);
                root.transform.SetPositionAndRotation(input.placement.position, input.placement.rotation);

                var shapeFrame = new GameObject("Shape Frame");
                shapeFrame.transform.SetParent(root.transform, false);
                shapeFrame.transform.SetLocalPositionAndRotation(localOffset, localRotation);

                var body = root.AddComponent<Rigidbody>();
                side = new PhysicsOwnerSide(positive, root, shapeFrame, body);
                AddColliders(products, input.inheritedMeshes, side, shapeFrame);

                side.Mass = mass.mass;
                side.CenterOfMass = mass.centerOfMass;
                side.InertiaTensor = mass.inertia;
                side.InertiaRotation = mass.inertiaRotation;

                bool fixedByAnchors = positive ? input.anchors.IsPositiveFixed : input.anchors.IsNegativeFixed;
                side.FixedByAnchors = fixedByAnchors;
                if (fixedByAnchors)
                {
                    // A fixed side takes no offset and no impulse (DESIGN 7.2), so it inherits no motion either.
                    side.LinearVelocity = float3.zero;
                    side.AngularVelocity = float3.zero;
                }
                else
                {
                    float3 centerWorld = input.placement.ToWorld(mass.centerOfMass);
                    side.LinearVelocity = FirstSplitVelocity(in input.sourceMotion, centerWorld);
                    side.AngularVelocity = input.sourceMotion.angularVelocity;
                }

                // After the colliders, and through the one place that writes them, so that the values the owner is
                // published with are the values this build decided and not a second copy of them.
                side.ApplyToBody();
                sideBuiltHook?.Invoke(positive);
            }
            catch
            {
                DestroyObject(root);
                throw;
            }

            return side;
        }

        /// <summary>
        /// The first-split velocity inheritance of DESIGN 7.2, about the render anchor:
        /// <c>v_anchor = v_sourceCOM + omega x (anchor - COM_source)</c> and
        /// <c>v_childCOM = v_anchor + omega x (COM_child - anchor)</c>, with the angular velocity carried over as it
        /// is. Momentum and energy are not conserved across the mass change and nothing here tries to.
        /// <para>
        /// This formula belongs to the first split — Source to Provisional, or Source straight to Final where
        /// Provisional is skipped — which is what this builder makes candidates for. The Provisional to Final handoff
        /// is a different thing and not this: there the physics actor is authoritative, its pose, centre-of-mass
        /// linear velocity and angular velocity are kept as they are, and only the shape, the centre of mass and the
        /// inertia are replaced on that same actor. Nothing here is for that path.
        /// </para>
        /// </summary>
        private static float3 FirstSplitVelocity(in PhysicsOwnerMotion motion, float3 centerOfMassWorld)
        {
            float3 atAnchor = motion.linearVelocity
                              + math.cross(motion.angularVelocity, motion.renderAnchor - motion.centerOfMass);
            return atAnchor + math.cross(motion.angularVelocity, centerOfMassWorld - motion.renderAnchor);
        }

        private static void AddColliders(
            PhysicsCutProducts products, IReadOnlyList<Mesh> inherited, PhysicsOwnerSide side, GameObject shapeFrame)
        {
            int count = products.PartCount(side.positive);
            int produced = 0;
            for (int i = 0; i < count; i++)
            {
                PhysicsCutPart part = products.Part(side.positive, i);
                Mesh mesh = part.borrowed ? inherited[part.inputConvex] : part.mesh;
                var collider = shapeFrame.AddComponent<MeshCollider>();

                // The profile before the mesh, and the same profile for every part (DESIGN 7.3): a collider given the
                // mesh first would cook it once with the wrong options.
                collider.cookingOptions = products.Cooking;
                collider.convex = true;
                collider.sharedMesh = mesh;
                side.Add(collider);
                if (!part.borrowed)
                {
                    produced++;
                }
            }

            side.ProducedColliderCount = produced;
        }

        private static bool ShapesArePresent(PhysicsCutProducts products, IReadOnlyList<Mesh> inherited)
        {
            foreach (bool positive in Sides)
            {
                int count = products.PartCount(positive);
                for (int i = 0; i < count; i++)
                {
                    PhysicsCutPart part = products.Part(positive, i);
                    if (!part.borrowed)
                    {
                        if (part.mesh == null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (inherited == null || part.inputConvex < 0 || part.inputConvex >= inherited.Count
                        || inherited[part.inputConvex] == null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The transform to the owner frame as a rotation and a translation, when that is what it is. A scale or a
        /// shear is refused: the colliders would then describe a different shape from the one the mass properties
        /// were integrated over, and neither of the two could be corrected without changing the other.
        /// </summary>
        private static bool TryRigid(float4x4 transform, out quaternion rotation, out float3 offset)
        {
            rotation = quaternion.identity;
            offset = float3.zero;
            if (!math.all(math.isfinite(transform.c0)) || !math.all(math.isfinite(transform.c1))
                || !math.all(math.isfinite(transform.c2)) || !math.all(math.isfinite(transform.c3)))
            {
                return false;
            }

            if (transform.c0.w != 0f || transform.c1.w != 0f || transform.c2.w != 0f || transform.c3.w != 1f)
            {
                return false;
            }

            float3 x = transform.c0.xyz, y = transform.c1.xyz, z = transform.c2.xyz;
            if (math.abs(math.lengthsq(x) - 1f) > RigidTolerance || math.abs(math.lengthsq(y) - 1f) > RigidTolerance
                || math.abs(math.lengthsq(z) - 1f) > RigidTolerance)
            {
                return false;
            }

            if (math.abs(math.dot(x, y)) > RigidTolerance || math.abs(math.dot(x, z)) > RigidTolerance
                || math.abs(math.dot(y, z)) > RigidTolerance)
            {
                return false;
            }

            if (math.dot(math.cross(x, y), z) <= 0f)
            {
                // A reflection is not a placement of the same shape.
                return false;
            }

            rotation = math.normalize(new quaternion(new float3x3(x, y, z)));
            offset = transform.c3.xyz;
            return math.all(math.isfinite(rotation.value));
        }

        internal static void DestroySide(PhysicsOwnerSide side)
        {
            if (side != null)
            {
                DestroyObject(side.Root);
            }
        }

        private static void DestroyObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
