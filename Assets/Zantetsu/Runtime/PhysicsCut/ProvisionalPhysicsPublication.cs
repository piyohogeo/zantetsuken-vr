using System;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What one Provisional publication was asked for.</summary>
    public struct ProvisionalPhysicsPublicationInput
    {
        /// <summary>The ledger that owns the logical state. It is only **read** here: a Provisional publishes nothing.</summary>
        public LogicalCutLedger ledger;

        /// <summary>Which fragment is which physics owner, and where the published pair is kept.</summary>
        public PhysicsOwnerRegistry registry;

        /// <summary>The accepted cut whose pair this is. The fragment it is of comes from its own record.</summary>
        public CutOperationId operation;

        /// <summary>
        /// The fragment the caller believes this operation is of. It is checked against the operation's own record and
        /// refused if they differ, so one fragment's physics can never be replaced for another fragment's cut. It may
        /// be left unset, and then the record alone says which fragment this is.
        /// </summary>
        public LogicalFragmentId source;

        /// <summary>The two actors, built and unpublished. On success it stops owning them.</summary>
        public ProvisionalOwnerCandidate candidate;

        /// <summary>
        /// The shape those actors were built from — the source owner's convexes and meshes as they were when the cut
        /// was accepted. It is checked against the owner the source has now, so a pair cannot be published over an
        /// owner that was replaced in the meantime. Ordinary movement changes neither this nor the ledger's authority.
        /// </summary>
        public PhysicsOwnerShape builtFrom;

        /// <summary>The FragmentRenderAnchor of the source, in the world, for the first-split velocity (DESIGN 7.2).</summary>
        public float3 renderAnchor;

        /// <summary>
        /// The separation impulse of each side, in newton-seconds, applied once here and never again (DESIGN 7.2).
        /// It moves only a side that no anchor fixes.
        /// <para>
        /// **The two values are the caller's**, one per child owner. What a side should be given is not decided here
        /// and is not these numbers: DESIGN 7.2 has the magnitude decided per child from that child's mass and a
        /// direction, and neither that decision nor any constant of its own is written into this path. The direction
        /// the impulse is applied in is unchanged — each free side away from the other along the adopted plane's
        /// normal.
        /// </para>
        /// </summary>
        public float positiveSeparationImpulse;

        /// <summary>The negative side's, by the same rule.</summary>
        public float negativeSeparationImpulse;
    }

    /// <summary>
    /// The Provisional publication of one accepted cut (DESIGN 7.1.1): the two temporary actors enter the physics
    /// scene in place of their source, in one main-thread update.
    /// <para>
    /// **No logical child is made.** The ledger publishes nothing here: the operation stays accepted, and both actors
    /// go on resolving to the one source fragment
    /// (<see cref="PhysicsOwnerRegistry.TryResolveSource"/>). The source keeps its owner record, withdrawn, for
    /// whoever retires it. This is not the Final publication and not the handoff to one, and the direct-final path is
    /// untouched by it.
    /// </para>
    /// <para>
    /// **The judgement is the ledger's own**, and it is the one the Final path asks for:
    /// <see cref="LogicalCutLedger.PreparePublication"/>. That is what says whether this cut may be published at all
    /// -- the source still live and still this cut's, **the physical-ownership authority unchanged since admission**,
    /// and the anchor distribution prepared -- and it reclaims a stale operation once, exactly as it would there. It
    /// is not repeated here and is not softened: an unprepared distribution is a refusal, not a pair that cannot be
    /// built, because a source whose distribution is unprepared has no sides for a display to draw and publishing its
    /// physics would leave the display with a body that stands nowhere.
    /// </para>
    /// <para>
    /// **The order is the point.** Everything that can be judged or reserved beforehand is: the fragment the cut is
    /// really of, the ledger's own preparation of it, the owner the source has, the shape the pair was built from,
    /// where the source is now, and the room the correspondence will need. Then, with nothing
    /// observing in between — no physics step, no admission, no query resolution, no renderer collection — the two
    /// actors enter the scene and are given their values, the source leaves it, and the pair becomes the
    /// correspondence both sides answer through.
    /// </para>
    /// <para>
    /// **This must be called from the main thread, outside the physics step** — from an ordinary update, not from a
    /// physics callback and not from inside a manual simulation. Nothing here steps, waits, sleeps or re-enters, and
    /// the switch itself calls nothing back: it is straight-line code over objects this call already has.
    /// </para>
    /// <para>
    /// **Failure is kept apart from refusal.** Everything the ledger refuses -- a moved authority, an operation that
    /// is not the active one, a distribution that was never prepared -- changes nothing at all: the source keeps its
    /// physics, nothing is aborted, and the cut is still there to be published when its turn comes. A pair that cannot
    /// be established is the other thing, and takes the ordinary continuation of DESIGN 7.1.1: the cut is aborted and
    /// the source retired. An unexpected exception **while the two actors are entering the scene** -- which is before
    /// the source leaves it -- takes both of them back out and is passed on, leaving the source where it was and the
    /// ownership where it was. **From the moment the source is withdrawn there is nothing to take back**: an owner that
    /// has left the scene is not put back, so the steps after that point are settled beforehand and an exception in
    /// them is passed on as an internal error, not converted into an unpublished failure.
    /// </para>
    /// </summary>
    public static class ProvisionalPhysicsPublication
    {
        /// <summary>
        /// Called once the switch is complete, for the tests that need an exception after the publication. The
        /// ordinary refusals and the room this needs are settled before it.
        /// </summary>
        internal static Action publishedHook;

        /// <summary>
        /// Called after each side has been put into the scene, for the tests that need an exception part way through
        /// the switch. True for the side that carries the constraint, which goes in last.
        /// </summary>
        internal static Action<bool> establishedHook;

        public static PhysicsPublicationOutcome TryPublish(
            in ProvisionalPhysicsPublicationInput input, out ProvisionalOwnerPair pair,
            out LogicalCutResultOutcome ledgerOutcome)
        {
            pair = null;
            ledgerOutcome = LogicalCutResultOutcome.NotActive;

            if (input.ledger == null || input.registry == null || input.candidate == null
                || input.candidate.IsDisposed || input.candidate.IsDetached || input.builtFrom == null
                || !input.operation.IsSet
                || !math.all(math.isfinite(input.renderAnchor))
                || !Usable(input.positiveSeparationImpulse) || !Usable(input.negativeSeparationImpulse))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            if (!input.ledger.TryGetOperation(input.operation, out LogicalCutOperation record))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            LogicalFragmentId source = record.source;
            if (input.source.IsSet && !input.source.Equals(source))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // The ledger's own preparation comes before this call's own comparisons, and before anything of the
            // physics is touched: its ordinary refusals -- a source whose authority moved, an operation that is not
            // the active one, a distribution that was never prepared -- surface here, where a stale operation is also
            // ended once. The judgement is the ledger's and is not repeated. Going first matters: an owner that was
            // replaced, and said so, has to reach this and be told it is stale, not be turned away by a comparison of
            // this call's own.
            ledgerOutcome = input.ledger.PreparePublication(input.operation);
            if (ledgerOutcome != LogicalCutResultOutcome.Applied)
            {
                return PhysicsPublicationOutcome.LedgerRefused;
            }

            if (!input.registry.TryGet(source, out PhysicsFragmentOwner sourceOwner) || sourceOwner.IsWithdrawn)
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // What the pair was built from has to be what the source is made of now. An owner that was replaced
            // without the ledger being told gets this far and is caught here rather than published over.
            if (!ReferenceEquals(input.builtFrom, sourceOwner.Shape))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // One accepted cut, one pair, and one pair to a source (DESIGN 7.1). Both are what the correspondence
            // would refuse when the pair is taken into it, and they are asked here instead: after the switch begins,
            // a refusal would come too late to be anything but an internal error.
            if (input.registry.TryGetProvisional(input.operation, out ProvisionalOwnerPair ofThisCut)
                && !ofThisCut.IsEnded)
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            if (input.registry.TryGetProvisionalOf(source, out ProvisionalOwnerPair already) && !already.IsEnded)
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            PhysicsOwnerSide positive = input.candidate.Positive;
            PhysicsOwnerSide negative = input.candidate.Negative;
            if (!Standing(positive) || !Standing(negative))
            {
                // Both actors have to be there before either is moved, and finding out that one is not must not touch
                // what is left of the other. This is a physics failure, not a mistake in the call.
                return Failed(in input);
            }

            // Read now, not from anything the candidate remembers: the source may have moved since it was built, and
            // ordinary motion is not staleness (DESIGN 8).
            PhysicsOwnerPlacement placement = sourceOwner.ReadPlacement();
            PhysicsOwnerMotion motion = sourceOwner.ReadMotion(input.renderAnchor);
            if (!placement.IsFinite || !motion.IsFinite)
            {
                return Failed(in input);
            }

            float3 planeNormal = math.mul(placement.rotation, math.normalizesafe(record.plane.xyz));

            // Each side with its own impulse, and each one's own mass turning that into its first velocity. The
            // direction is the existing one and is not decided here.
            positive.Reposition(placement, in motion, planeNormal, input.positiveSeparationImpulse);
            negative.Reposition(placement, in motion, planeNormal, input.negativeSeparationImpulse);

            // The record the correspondence will hold, and the room for it, made before the switch rather than after
            // it, so that what is left afterwards is the switch itself and nothing more. Both sides are given the
            // source's own display correspondence: it is a mapping into an owner's coordinates, and both stand at the
            // placement the source has, so it carries as it is. Nothing of the display's own is added to it.
            var published = new ProvisionalOwnerPair(
                input.operation, source, positive, negative,
                input.candidate.PositiveShape, input.candidate.NegativeShape, input.candidate.Separation,
                sourceOwner.GeometryLocalToOwner);
            input.registry.ReserveProvisional();

            // ---- one main-thread update from here: nothing steps, admits, resolves or collects in between ----

            // **The two actors enter the scene while the source is still in it.** Up to the end of this region the
            // source has not moved, so taking the pair back out really does leave everything as it was: a failure and
            // an exception both end with nothing of the pair in the scene and the source still standing. That is the
            // whole of what can be taken back, which is why it ends here.
            try
            {
                // The side that does not carry the constraint enters first, so that the constraint's own side never
                // enters while the body it names is out of the scene. Which side that is is a detail of how the pair
                // was built, and nothing here depends on the choice beyond this order.
                if (!Establish(negative, false) || !Establish(positive, true))
                {
                    Withdraw(positive);
                    Withdraw(negative);
                    return Failed(in input);
                }
            }
            catch (Exception)
            {
                // One half of a pair must not be left standing beside the source it was to replace.
                Withdraw(positive);
                Withdraw(negative);
                throw;
            }

            // ---- the switch. Nothing from here is taken back: the moment the source leaves the scene, "as it was" is
            // not a state this call can return to -- an owner that has been withdrawn is not put back. Everything that
            // decides whether these steps may happen at all was settled above: the ledger's judgement, the room for
            // the correspondence, and that neither this cut nor this source already has a pair. ----

            // The source leaves the scene. It is not destroyed and gives nothing back here: its record stays, so what
            // it holds -- the meshes both sides are sharing -- stays held until it is retired in its turn.
            sourceOwner.Withdraw();

            input.registry.AddProvisional(published);

            // The one point where the two actors stop being the candidate's.
            input.candidate.Detach();

            pair = published;
            publishedHook?.Invoke();
            return PhysicsPublicationOutcome.Published;
        }

        /// <summary>
        /// The ordinary continuation of a physics failure (DESIGN 7.1.1, 7.1.3): the cut is aborted and the source it
        /// was of is retired. The candidate is still the caller's to give up.
        /// </summary>
        private static PhysicsPublicationOutcome Failed(in ProvisionalPhysicsPublicationInput input)
        {
            FinalPhysicsPublication.AbortAfterPhysicsFailure(input.ledger, input.registry, input.operation);
            return PhysicsPublicationOutcome.PhysicsNotEstablished;
        }

        /// <summary>
        /// Puts one side into the scene and gives its body the values the build decided. A body that is not in the
        /// scene cannot hold a centre of mass or an inertia, so this is where those values become real.
        /// </summary>
        private static bool Establish(PhysicsOwnerSide side, bool carriesTheConstraint)
        {
            side.Root.SetActive(true);
            side.ApplyToBody();
            establishedHook?.Invoke(carriesTheConstraint);
            return side.Root.activeInHierarchy;
        }

        private static void Withdraw(PhysicsOwnerSide side)
        {
            if (side?.Root != null)
            {
                side.Root.SetActive(false);
            }
        }

        private static bool Standing(PhysicsOwnerSide side)
        {
            return side != null && side.Root != null && side.Body != null;
        }

        private static bool Usable(float impulse)
        {
            return impulse >= 0f && math.isfinite(impulse);
        }
    }
}
