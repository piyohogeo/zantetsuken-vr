using System;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What one handoff was asked for.</summary>
    public struct FinalHandoffInput
    {
        /// <summary>The ledger that owns the logical state. Its judgement is the one that decides.</summary>
        public LogicalCutLedger ledger;

        /// <summary>Which fragment is which physics owner, where the published pair is, and where the children go.</summary>
        public PhysicsOwnerRegistry registry;

        /// <summary>The accepted cut whose Provisional pair is to become its Final children.</summary>
        public CutOperationId operation;

        /// <summary>
        /// The finished cut of that operation. **It stays the caller's until this succeeds**: a handoff that does not
        /// happen leaves the products untouched, for the caller to keep or give up itself.
        /// </summary>
        public PhysicsCutProducts products;

        /// <summary>
        /// The shape the products were cut from — the source owner's convexes and meshes as they were when the cut was
        /// accepted. The borrowed parts of the products are ranges in **its** bank and meshes **it** holds, so it is
        /// read here and must be alive until this call has built both final shapes.
        /// </summary>
        public PhysicsOwnerShape cutFrom;

        /// <summary>
        /// The mass of the source as it was **when the cut was accepted** — the snapshot the cut was asked for and
        /// classified with, which DESIGN 7.2 makes the parent mass the two final masses must add up to.
        /// <para>
        /// It is given by whoever holds the request and is not worked out again here. The two published actors carry a
        /// temporary mass between them, split by a box approximation and rounded to what a Rigidbody keeps, and
        /// anything that touches those bodies in the meantime changes it; adding the two up would let that stand in
        /// for the parent and decide whether the final set is usable.
        /// </para>
        /// </summary>
        public double parentMass;
    }

    /// <summary>
    /// The handoff from a published Provisional pair to the Final publication of the same cut (DESIGN 7.1.2, 7.2): the
    /// two actors that are already in the scene are given the final shape and mass properties, and the ledger publishes
    /// the two logical children they become.
    /// <para>
    /// **The actor is the authority and is not rebuilt.** Each side keeps its object, its Rigidbody and its place in
    /// the world; what changes is what is on it. Its pose is not touched, its centre-of-mass velocity and angular
    /// velocity are carried across as they are, and nothing is converted for the new centre of mass or wound back to
    /// where the cut was accepted. No separation impulse is applied again — it was applied once, when the pair was
    /// published. The anchor distribution settled at acceptance is the one the children inherit; it is not worked out
    /// again. The sibling constraint ends with the pair, and nothing compensates for its going.
    /// </para>
    /// <para>
    /// **Everything that can be judged, built or reserved comes first**: the ledger's own preparation, the final mass
    /// properties of both sides by the rule a direct final split uses, **both sides' final colliders, made and cooked
    /// on their actors and left disabled**, both final shapes, and the room the correspondence needs. Nothing of the
    /// published configuration is broken while any of that can still fail, so a second side that cannot be prepared
    /// never leaves the first one already changed.
    /// </para>
    /// <para>
    /// Then, with nothing observing in between — no physics step, no admission, no query resolution, no renderer
    /// collection — the switch: on each actor the old colliders stop answering and the prepared ones begin, the mass
    /// properties are replaced, the ledger publishes, the ownership of the cut's meshes moves, each child is
    /// registered as the owner of the actor it is, the pair leaves the correspondence **without its actors being
    /// destroyed**, and the replaced source's own owner is retired.
    /// </para>
    /// <para>
    /// **Geometry takes no part in it.** No geometry cut or commit is waited for, and publishing the physics returns no
    /// unit of the geometry responsibility: what the ledger does with the operation afterwards is the ledger's, and
    /// this call does not end it.
    /// </para>
    /// <para>
    /// **Refusal, failure and exception are three things.** A ledger that refuses — a moved authority, an operation
    /// that is not the active one, a distribution never prepared — changes nothing at all, and a stale result retires
    /// no fragment. A final set that cannot be established is a physics failure and takes the ordinary continuation of
    /// DESIGN 7.1.1, which is the caller's to run, not this call's: **nothing is aborted here**, so that a caller
    /// holding the products decides what becomes of them. An exception during the preparation gives back only what the
    /// preparation had taken, and the published pair stands as it was.
    /// </para>
    /// <para>
    /// **An exception once the switch has begun gives back nothing itself, and leaves nothing without an owner.** The
    /// first thing the switch does is give the two final shapes to the pair, which gives back the temporary ones it
    /// was published with at that same moment. So from then on the shapes the actors are being put on are held by
    /// something an ending can find: ending the published pair is what takes them back, and it does so with the actors
    /// that stand on them. Nothing is disposed here, and nothing is left in a local variable that only this call could
    /// have reached.
    /// </para>
    /// <para>
    /// **The ownership of the cut's meshes moves where the actors do.** It is taken at the point the pair leaves the
    /// correspondence, so that "the cut still has its pair" and "the products are still the caller's" say the same
    /// thing, and a caller that has to tell the two sides of the switch apart can ask the correspondence.
    /// </para>
    /// <para>
    /// **The hold this call takes on the products is let go once on every path** — after the last thing that reads
    /// them, however the call ended. It gives nothing back by itself unless the ownership moved and nothing else holds
    /// them, which is what makes "the cut's meshes go back when the last child is retired" true.
    /// </para>
    /// </summary>
    public static class FinalHandoffPublication
    {
        /// <summary>
        /// Called once the switch is complete, for the tests that need an exception after it. The ordinary refusals and
        /// the room this needs are settled before it.
        /// </summary>
        internal static Action publishedHook;

        /// <summary>
        /// Called as each side's colliders have been prepared, with that side, for the tests that need the second
        /// side's preparation to fail. It is inside the preparation, before anything of the published pair is touched.
        /// </summary>
        internal static Action<bool> preparingHook;

        /// <summary>
        /// Called as each side has been given its final shape and mass properties, with that side, for the tests that
        /// need an exception **in the middle of the switch** -- after the pair has taken the final shapes and before
        /// anything is published.
        /// </summary>
        internal static Action<bool> establishedHook;

        public static PhysicsPublicationOutcome TryHandOff(
            in FinalHandoffInput input,
            out LogicalFragmentId positive,
            out LogicalFragmentId negative,
            out LogicalCutResultOutcome ledgerOutcome)
        {
            positive = default;
            negative = default;
            ledgerOutcome = LogicalCutResultOutcome.NotActive;

            if (input.ledger == null || input.registry == null || input.products == null || input.cutFrom == null
                || input.cutFrom.IsFreed || !input.operation.IsSet
                || !(input.parentMass > 0.0) || !math.isfinite(input.parentMass))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            if (!input.ledger.TryGetOperation(input.operation, out LogicalCutOperation record))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            LogicalFragmentId source = record.source;
            if (!input.registry.TryGetProvisional(input.operation, out ProvisionalOwnerPair pair) || pair.IsEnded)
            {
                // There is no published pair to hand over. This is the handoff, not the direct-final path.
                return PhysicsPublicationOutcome.InvalidInput;
            }

            if (!pair.Source.Equals(source))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // The source's own owner, withdrawn when the pair was published, is still registered: it holds the shape
            // the cut was of, and it is what is retired at the end.
            if (!input.registry.TryGet(source, out PhysicsFragmentOwner sourceOwner)
                || !ReferenceEquals(input.cutFrom, sourceOwner.Shape))
            {
                // The products were cut from a shape that is not this fragment's any more: an owner was replaced
                // without the ledger being told. It is caught here rather than applied.
                return PhysicsPublicationOutcome.InvalidInput;
            }

            PhysicsOwnerSide positiveSide = pair.Positive;
            PhysicsOwnerSide negativeSide = pair.Negative;
            if (!Usable(positiveSide) || !Usable(negativeSide))
            {
                return PhysicsPublicationOutcome.PhysicsNotEstablished;
            }

            // The ledger's own preparation, before anything of the physics is touched: its ordinary refusals surface
            // here, where a stale operation is also ended once, and the room for the two children is taken.
            ledgerOutcome = input.ledger.PreparePublication(input.operation);
            if (ledgerOutcome != LogicalCutResultOutcome.Applied)
            {
                return PhysicsPublicationOutcome.LedgerRefused;
            }

            // The final mass properties, against the parent mass this cut was accepted with. What both sides
            // would ask the same -- the products, that neither side is empty, the rigid frame -- is asked once here,
            // and each side's own mass, centre and inertia follow in that one frame.
            if (!PhysicsOwnerBuilder.TryFinalMassFrame(
                    input.products, input.parentMass,
                    out quaternion localRotation, out float3 localOffset, out PhysicsOwnerBuildOutcome _)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(
                    input.products, input.parentMass, true, localRotation, localOffset,
                    out double positiveMass, out float3 positiveCentre, out float3 positiveInertia,
                    out quaternion positiveInertiaRotation, out PhysicsOwnerBuildOutcome _)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(
                    input.products, input.parentMass, false, localRotation, localOffset,
                    out double negativeMass, out float3 negativeCentre, out float3 negativeInertia,
                    out quaternion negativeInertiaRotation, out PhysicsOwnerBuildOutcome _)
                || !PhysicsOwnerBuilder.FinalShapesArePresent(input.products, sourceOwner.Shape.Meshes))
            {
                // The final set of this cut cannot be established. Nothing has been touched: the pair is still
                // standing, the products are still the caller's, and what follows from that is the caller's too.
                return PhysicsPublicationOutcome.PhysicsNotEstablished;
            }

            // One counter for these products, taken once by this call and let go once below. It owns nothing yet: the
            // ownership moves at the one point where the handoff has succeeded.
            PhysicsShapeSource productsSource = PhysicsShapeSource.For(input.products);
            productsSource.Acquire();
            try
            {
                PhysicsOwnerShape positiveShape = null;
                PhysicsOwnerShape negativeShape = null;
                PreparedSideColliders positivePrepared = null;
                PreparedSideColliders negativePrepared = null;
                try
                {
                    // Both sides' colliders, made and cooked on the actors they are for and **left disabled**: the
                    // published configuration is still what answers, and a failure of the second side leaves the first
                    // one exactly as it was.
                    positivePrepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                        input.products, sourceOwner.Shape.Meshes, positiveSide, pair.PositiveShape, localRotation, localOffset, sourceOwner.Shape);
                    preparingHook?.Invoke(true);
                    negativePrepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                        input.products, sourceOwner.Shape.Meshes, negativeSide, pair.NegativeShape, localRotation, localOffset, sourceOwner.Shape);
                    preparingHook?.Invoke(false);

                    // The borrowed parts are read here, from the shape the cut was of, and each final shape takes its
                    // own holds on whatever it goes on using. After this the handoff needs nothing of that shape.
                    positiveShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, input.products, productsSource, true);
                    negativeShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, input.products, productsSource, false);

                    // The room the correspondence needs, made before the switch. Each child is the very actor that was
                    // its Provisional side, and each keeps the source's display correspondence: the actors are not
                    // moved, so what is drawn from them is drawn where it already was.
                    input.registry.Reserve(2);
                }
                catch (Exception)
                {
                    // Still the preparation: nothing of the published pair has changed. Only what this call had made
                    // goes back, and the error is passed on.
                    negativePrepared?.Withdraw();
                    positivePrepared?.Withdraw();
                    Discard(positiveShape, negativeShape);
                    throw;
                }

                // ---- one main-thread update from here: nothing steps, admits, resolves or collects in between ----

                // The first thing, before the actors stand on any of it: the pair holds the final shapes, and the
                // temporary ones it was published with go back. From here everything the switch uses belongs to
                // something an ending can find.
                pair.TakeFinalShapes(positiveShape, negativeShape);
                try
                {
                    // The same actors, given the final shape. The pose is not touched; the motion is read and written
                    // back, so the centre-of-mass velocity and the angular velocity are the ones the solver had a
                    // moment ago and nothing is converted for the centre of mass that has just changed.
                    Establish(
                        positiveSide, positivePrepared, positiveMass, positiveCentre, positiveInertia,
                        positiveInertiaRotation, localRotation, localOffset);
                    establishedHook?.Invoke(true);
                    Establish(
                        negativeSide, negativePrepared, negativeMass, negativeCentre, negativeInertia,
                        negativeInertiaRotation, localRotation, localOffset);
                    establishedHook?.Invoke(false);

                    ledgerOutcome = input.ledger.Publish(input.operation, out positive, out negative);
                }
                catch (Exception)
                {
                    // The switch has begun and there is no way back to what was there. Nothing is given back here:
                    // the pair holds the shapes the actors are on, and ending that pair -- which the caller does, with
                    // the actors -- is what takes them back. The error is passed on as an internal error.
                    positive = default;
                    negative = default;
                    throw;
                }

                if (ledgerOutcome != LogicalCutResultOutcome.Applied)
                {
                    // The ledger settles everything that can fail in its own preparation, so this is not an ordinary
                    // outcome. As above, the pair is what holds the shapes the actors are on, and ending it is what
                    // gives them back; nothing is disposed here.
                    positive = default;
                    negative = default;
                    return PhysicsPublicationOutcome.LedgerRefused;
                }

                // ---- published. From here nothing is caught and nothing of the children's is given back. ----

                var positiveOwner = new PhysicsFragmentOwner(
                    positiveSide.Root, positiveSide.Body, positiveShape, positiveSide.FixedByAnchors,
                    pair.GeometryLocalToOwner);
                var negativeOwner = new PhysicsFragmentOwner(
                    negativeSide.Root, negativeSide.Body, negativeShape, negativeSide.FixedByAnchors,
                    pair.GeometryLocalToOwner);

                // The children first, so both have their owner -- and each actor resolves to the child it is -- before
                // anything of the old correspondence goes.
                input.registry.Add(positive, positiveOwner);
                input.registry.Add(negative, negativeOwner);

                // The pair leaves the correspondence and **its actors are not destroyed**: they are the children now.
                // The sibling constraint goes with the pair, and the final shapes go with the actors, to the owners
                // registered just above.
                input.registry.TryHandOverProvisional(input.operation, out PhysicsOwnerSide _, out PhysicsOwnerSide _);

                // And with them, at that same point, the ownership of the cut's meshes: "the cut still has its pair"
                // and "the products are still the caller's" are one question, so that a caller which has to tell the
                // two sides of this switch apart can ask the correspondence and be right.
                productsSource.TakeOwnership();

                // And the replaced source's own owner ends: out of the scene long since, destroyed now, and its shape
                // given up. What the children are still using it does not take with it.
                input.registry.Retire(source);

                publishedHook?.Invoke();
                return PhysicsPublicationOutcome.Published;
            }
            finally
            {
                // This call's own hold, taken once and let go once however it ended -- after the last read of the
                // products. A child that kept a shape has a hold of its own by now; if the ownership never moved,
                // letting go gives nothing back at all.
                productsSource.Release();
            }
        }

        /// <summary>
        /// Gives one actor the final shape and mass properties of its side, keeping everything else it has: its place
        /// in the world, and the motion the solver gave it. Everything used here was made beforehand.
        /// </summary>
        private static void Establish(
            PhysicsOwnerSide side,
            PreparedSideColliders prepared,
            double mass,
            float3 centreOfMass,
            float3 inertia,
            quaternion inertiaRotation,
            quaternion localRotation,
            float3 localOffset)
        {
            // The motion as it is now. A fixed side is kinematic and has none to keep.
            float3 linear = side.FixedByAnchors ? float3.zero : (float3)side.Body.linearVelocity;
            float3 angular = side.FixedByAnchors ? float3.zero : (float3)side.Body.angularVelocity;

            // The old colliders stop answering, the shape frame takes the products' numerical local frame, and the
            // prepared colliders begin. The old ones are destroyed afterwards, having been disabled first.
            prepared.Adopt(localRotation, localOffset);

            side.Mass = mass;
            side.CenterOfMass = centreOfMass;
            side.InertiaTensor = inertia;
            side.InertiaRotation = inertiaRotation;
            side.LinearVelocity = linear;
            side.AngularVelocity = angular;

            // The one place that writes a body, so the values it is published with are the values decided above. No
            // impulse is added: the separation impulse was applied once, at the Provisional publication. The flags
            // the publication set on this same actor (automatic mass off, kinematic as the anchors decided) stand.
            side.ApplyMassAndMotionToBody();
        }

        private static void Discard(PhysicsOwnerShape positive, PhysicsOwnerShape negative)
        {
            positive?.Dispose();
            negative?.Dispose();
        }

        private static bool Usable(PhysicsOwnerSide side)
        {
            return side != null && side.Root != null && side.Body != null && side.ShapeFrame != null;
        }
    }
}
