using System;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How one attempt at the final physics and logical publication of a cut ended.</summary>
    public enum PhysicsPublicationOutcome
    {
        /// <summary>No attempt was made.</summary>
        NotAttempted = 0,

        /// <summary>Both owners are in the scene, the operation and its two children are published, and the
        /// correspondence is the children's.</summary>
        Published = 1,

        /// <summary>
        /// The call itself does not hold together: something it needs was not given, the operation is not one this
        /// ledger has, the fragment named is not the one that operation is of, the source has no physics owner, or the
        /// products were cut from a shape that is not that owner's any more. Nothing is changed and nothing is
        /// aborted — this is a mistake in the call, not a physics failure.
        /// </summary>
        InvalidInput = 2,

        /// <summary>
        /// The ledger refused, with its own reason alongside. Nothing of the physics changed: the source is as it was
        /// and the candidate is still the caller's. A stale result retires nothing.
        /// </summary>
        LedgerRefused = 3,

        /// <summary>
        /// The physics of the two sides could not be established. Nothing is published, and this is taken to the
        /// ordinary continuation of DESIGN 7.1.1: the operation is aborted and the source retired through the ledger
        /// and the registry. The reported ledger outcome is that abort's. The candidate is still the caller's to give
        /// up, and so are the products.
        /// </summary>
        PhysicsNotEstablished = 4,
    }

    /// <summary>What one publication was asked for.</summary>
    public struct FinalPhysicsPublicationInput
    {
        /// <summary>The ledger that owns the logical state. Its judgement is the one that decides.</summary>
        public LogicalCutLedger ledger;

        /// <summary>Which fragment is which physics owner. The source is read from it and the children are added.</summary>
        public PhysicsOwnerRegistry registry;

        /// <summary>The admitted operation to publish. The fragment it replaces comes from its own record.</summary>
        public CutOperationId operation;

        /// <summary>
        /// The fragment the caller believes this operation is of. It is checked against the operation's own record and
        /// refused if they differ, so that one fragment's owner can never be retired for another fragment's cut. It may
        /// be left unset, and then the record alone says which fragment this is.
        /// </summary>
        public LogicalFragmentId source;

        /// <summary>The two owners, built and unpublished. On success it stops owning them.</summary>
        public PhysicsOwnerCandidate candidate;

        /// <summary>
        /// The cut they were built from. **It stays the caller's until this succeeds**: a call that does not publish
        /// leaves the products untouched, for the caller to go on using or to dispose itself.
        /// </summary>
        public PhysicsCutProducts products;

        /// <summary>
        /// The shape those products were cut from — the source owner's convexes and meshes as they were at admission.
        /// It is checked against the owner the source has now, so a result cannot be applied to an owner that was
        /// replaced in the meantime. Ordinary movement changes neither this nor the ledger's authority; replacing an
        /// owner changes both, once whoever replaced it has told the ledger.
        /// </summary>
        public PhysicsOwnerShape cutFrom;

        /// <summary>The FragmentRenderAnchor of the source, in the world, for the first-split velocity.</summary>
        public float3 renderAnchor;

        /// <summary>
        /// The separation impulse of a direct final split (DESIGN 7.2), in newton-seconds, applied once here and
        /// never again. It moves only a side that no anchor fixes. **The value is the caller's**: what a product
        /// should use is not decided here and is not this number.
        /// </summary>
        public float separationImpulse;
    }

    /// <summary>
    /// The final physics and the logical publication of one direct final split, in one main-thread update
    /// (DESIGN 7.1.2).
    /// <para>
    /// **This is the direct-final path**: the case where the whole final set can be established before any Provisional
    /// is published (DESIGN 7.1.1). It is not a way to keep the old physics and wait for the final one, and it is not
    /// the Provisional to Final handoff.
    /// </para>
    /// <para>
    /// **The order is the point.** Everything that can be judged, built or reserved beforehand is: the fragment the
    /// operation is really of, the owner it has and the shape the cut was of, the ledger's own preparation, the two
    /// sides' shapes, the two owner records and the room for them. Then, with nothing observing in between — no
    /// physics step, no admission, no query resolution, no renderer collection — the owners enter the scene and are
    /// given their values, the ledger publishes, the ownership of the cut's meshes moves, the correspondence becomes
    /// the children's and the source's physics ends.
    /// </para>
    /// <para>
    /// **Nothing is assumed to be incapable of failing, and the two sides of the publication are not treated alike.**
    /// Before it, a physics failure takes the ordinary continuation of DESIGN 7.1.1 — the operation is aborted and the
    /// source retired — and an unexpected exception gives back what this call had made and is passed on to the
    /// caller's own handling of internal errors, unread as an ordinary outcome. A refusal by the ledger leaves the
    /// source exactly as it was. After the publication there is nothing to convert: the children are published, that
    /// fact stands, and an exception there is passed on with nothing of theirs given back.
    /// </para>
    /// <para>
    /// Geometry takes no part in this. The children are published without any, they can be cut again at once, and the
    /// geometry responsibility stays with the cut DAG: until a geometry commit, what is drawn is still the source's
    /// registration, so this is not the whole of DESIGN 7.1.2.
    /// </para>
    /// <para>
    /// **What the display follows switches with the correspondence, and at the same moment.** A lineage whose display
    /// follows its owner says, on its first owner, where its display geometry sits in that owner's coordinates; both
    /// children are given that same correspondence, because a publication puts them at the very placement the source
    /// had rather than rebuilding a frame for each. The two child records carry it and are made before anything is
    /// published, so the switch itself stays what it was — the children's records go in, the source's goes out — with
    /// no second step and nothing to prepare in between. A lineage that says nothing about its display is unaffected
    /// and its children say nothing either.
    /// </para>
    /// </summary>
    public static class FinalPhysicsPublication
    {
        /// <summary>
        /// Called once the switch is complete, for the tests that need an exception after the publication. The
        /// ordinary refusals and the room a publication needs are settled before it, and an unexpected exception after
        /// it is passed on as an internal error. What this hook tries is an exception at that one point, just after the
        /// switch: it does not put each step of the switch itself — the source's withdrawal, its destruction, the
        /// resources it gives back — to the test.
        /// </summary>
        internal static Action publishedHook;

        public static PhysicsPublicationOutcome TryPublish(
            in FinalPhysicsPublicationInput input,
            out LogicalFragmentId positive,
            out LogicalFragmentId negative,
            out LogicalCutResultOutcome ledgerOutcome)
        {
            positive = default;
            negative = default;
            ledgerOutcome = LogicalCutResultOutcome.NotActive;

            if (input.ledger == null || input.registry == null || input.products == null || input.cutFrom == null
                || input.candidate == null || input.candidate.IsDisposed || !input.operation.IsSet
                || !math.all(math.isfinite(input.renderAnchor))
                || !(input.separationImpulse >= 0f) || !math.isfinite(input.separationImpulse))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            if (!input.ledger.TryGetOperation(input.operation, out LogicalCutOperation record))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // The fragment this cut is of is the operation's own. A caller that says which one it thinks it is has to
            // agree with the record: otherwise one fragment's owner could be retired for another fragment's cut.
            LogicalFragmentId source = record.source;
            if (input.source.IsSet && !input.source.Equals(source))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // The ledger's own preparation comes before this call's own comparisons, and before anything of the
            // physics is touched: its ordinary refusals — a source whose authority moved, an operation that is not the
            // active one, a distribution that was never prepared — surface here, where a stale operation is also ended
            // once and its budget returned. The judgement is the ledger's and is not repeated here. Going first
            // matters: an owner that was replaced, and said so, has to reach this and be told it is stale, not be
            // turned away by a comparison of this call's own.
            ledgerOutcome = input.ledger.PreparePublication(input.operation);
            if (ledgerOutcome != LogicalCutResultOutcome.Applied)
            {
                return PhysicsPublicationOutcome.LedgerRefused;
            }

            if (!input.registry.TryGet(source, out PhysicsFragmentOwner sourceOwner) || sourceOwner.IsWithdrawn)
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // What was cut has to be what the source is made of now. An owner that was replaced without the ledger
            // being told still gets this far, and is caught here rather than applied. Moving changes neither this nor
            // the authority above.
            if (!ReferenceEquals(input.cutFrom, sourceOwner.Shape))
            {
                return PhysicsPublicationOutcome.InvalidInput;
            }

            // Read now, not from anything the candidate remembers: the source may have moved since it was built, and
            // ordinary motion is not a reason to refuse anything.
            PhysicsOwnerPlacement placement = sourceOwner.ReadPlacement();
            PhysicsOwnerMotion motion = sourceOwner.ReadMotion(input.renderAnchor);
            if (!placement.IsFinite || !motion.IsFinite)
            {
                return Failed(in input, ref ledgerOutcome);
            }

            float3 planeNormal = math.mul(placement.rotation, math.normalizesafe(record.plane.xyz));

            // One counter for these products, taken once by this call. It owns nothing yet: the ownership moves at the
            // one point below, where the publication has succeeded.
            PhysicsShapeSource productsSource = PhysicsShapeSource.For(input.products);
            productsSource.Acquire();
            PhysicsOwnerShape positiveShape = null;
            PhysicsOwnerShape negativeShape = null;
            try
            {
                PhysicsFragmentOwner positiveOwner;
                PhysicsFragmentOwner negativeOwner;
                if (!Usable(input.candidate.Positive) || !Usable(input.candidate.Negative))
                {
                    // Both owners have to be there before either is moved, and finding out that one is not must not
                    // touch what is left of the other. This is a physics failure, not an internal error.
                    return Failed(in input, ref ledgerOutcome);
                }

                try
                {
                    positiveShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, input.products, productsSource, true);
                    negativeShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, input.products, productsSource, false);

                    // The records the correspondence will hold, and the room for them, made before the publication
                    // rather than after it, so that what is left afterwards is the switch itself and nothing more.
                    // Each child is given the source's own display correspondence: it is a mapping into the owner's
                    // coordinates, and both children begin at the placement the source has, so it carries as it is.
                    // Nothing is taken from where the lineage was registered, and nothing of the display's
                    // separation is added to it here.
                    Matrix4x4? displayFrame = sourceOwner.GeometryLocalToOwner;
                    positiveOwner = new PhysicsFragmentOwner(
                        input.candidate.Positive.Root, input.candidate.Positive.Body, positiveShape,
                        input.candidate.Positive.FixedByAnchors, displayFrame);
                    negativeOwner = new PhysicsFragmentOwner(
                        input.candidate.Negative.Root, input.candidate.Negative.Body, negativeShape,
                        input.candidate.Negative.FixedByAnchors, displayFrame);
                    input.registry.Reserve(2);
                }
                catch (Exception)
                {
                    // Making room, copying a B-rep or taking a hold does not fail as part of the design. What this
                    // call had made goes back, and the error is passed on rather than read as a physics failure.
                    Discard(positiveShape, negativeShape);
                    throw;
                }

                // ---- one main-thread update from here: nothing steps, admits, resolves or collects in between ----
                try
                {
                    input.candidate.Reposition(placement, in motion, planeNormal, input.separationImpulse);
                    if (!Establish(input.candidate))
                    {
                        Withdraw(input.candidate);
                        Discard(positiveShape, negativeShape);
                        return Failed(in input, ref ledgerOutcome);
                    }

                    ledgerOutcome = input.ledger.Publish(input.operation, out positive, out negative);
                }
                catch (Exception)
                {
                    // Before the publication, and it threw: the owners come back out of the scene and what this call
                    // made goes back. Nothing has been published — the ledger settles everything that can fail before
                    // its first change — and the error is passed on rather than read as a physics failure.
                    Withdraw(input.candidate);
                    Discard(positiveShape, negativeShape);
                    positive = default;
                    negative = default;
                    throw;
                }

                if (ledgerOutcome != LogicalCutResultOutcome.Applied)
                {
                    // The ledger is the one that decides, and it decided last. The owners go back out of the scene,
                    // the source keeps its own physics, and nothing is aborted for a refusal that is not a physics
                    // failure.
                    Withdraw(input.candidate);
                    Discard(positiveShape, negativeShape);
                    positive = default;
                    negative = default;
                    return PhysicsPublicationOutcome.LedgerRefused;
                }

                // ---- published. The ordinary refusals and the room for this were settled before; from here nothing
                // is caught and nothing of the children's is given back. The two children exist in the ledger, and an
                // exception in what follows is an internal error to be passed on, not an ordinary outcome and not a
                // reason to abort what is already published. ----

                // The one point where the cut's meshes stop being the caller's.
                productsSource.TakeOwnership();

                // The correspondence first, so that both children have their owner before the old one goes. The
                // children hold the inherited shapes already, so ending the source gives back only what nothing else
                // is using, and it leaves the physics scene before it is destroyed. What a display following this
                // lineage sees switches here too and nowhere else: from this point each child stands where its own
                // owner does, and the replaced source is not kept alive for anything to be drawn from.
                input.registry.Add(positive, positiveOwner);
                input.registry.Add(negative, negativeOwner);
                input.candidate.Detach();
                input.registry.Retire(source);
                publishedHook?.Invoke();
            }
            finally
            {
                // This call's own hold, taken once and let go once however it ended. A side that kept its shape has a
                // hold of its own by now; one that did not gave it back with the shape, and if the ownership never
                // moved, letting go gives nothing back at all.
                productsSource.Release();
            }

            return PhysicsPublicationOutcome.Published;
        }

        /// <summary>
        /// The ordinary continuation of a physics failure (DESIGN 7.1.1, 7.1.3): the operation is aborted and the
        /// source it was of is retired, through the ledger and then the registry. A stale or no-longer-active result
        /// retires nothing — the fragment that is there now is not the one this failure was about.
        /// </summary>
        public static LogicalCutResultOutcome AbortAfterPhysicsFailure(
            LogicalCutLedger ledger, PhysicsOwnerRegistry registry, CutOperationId operation)
        {
            if (ledger == null || registry == null)
            {
                throw new ArgumentNullException(ledger == null ? nameof(ledger) : nameof(registry));
            }

            if (!ledger.TryGetOperation(operation, out LogicalCutOperation record))
            {
                return LogicalCutResultOutcome.NotActive;
            }

            LogicalFragmentId source = record.source;
            LogicalCutResultOutcome outcome = ledger.Abort(operation);
            if (outcome == LogicalCutResultOutcome.Applied)
            {
                // The ledger retired the source, so its physics ends with it: out of the scene first, then destroyed.
                registry.Retire(source);
            }

            return outcome;
        }

        private static PhysicsPublicationOutcome Failed(
            in FinalPhysicsPublicationInput input, ref LogicalCutResultOutcome ledgerOutcome)
        {
            ledgerOutcome = AbortAfterPhysicsFailure(input.ledger, input.registry, input.operation);
            return PhysicsPublicationOutcome.PhysicsNotEstablished;
        }

        /// <summary>Gives up the shapes this call had made. Their holds go back with them.</summary>
        private static void Discard(PhysicsOwnerShape positiveShape, PhysicsOwnerShape negativeShape)
        {
            positiveShape?.Dispose();
            negativeShape?.Dispose();
        }

        private static bool Usable(PhysicsOwnerSide side)
        {
            return side != null && side.Root != null && side.Body != null;
        }

        /// <summary>
        /// Puts both owners into the scene and gives their bodies the values the build decided. A body that is not in
        /// the scene cannot hold a centre of mass or an inertia, so this is where those values become real. What is
        /// asked afterwards is only that both actors are there and active: the numbers are the build's, and no
        /// tolerance of this step's own stands between them and the solver.
        /// </summary>
        private static bool Establish(PhysicsOwnerCandidate candidate)
        {
            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide side = candidate.Side(positive);
                if (!Usable(side))
                {
                    return false;
                }

                side.Root.SetActive(true);
                side.ApplyToBody();
            }

            return Standing(candidate.Positive) && Standing(candidate.Negative);
        }

        private static bool Standing(PhysicsOwnerSide side)
        {
            return Usable(side) && side.Root.activeInHierarchy;
        }

        /// <summary>Takes the owners back out of the scene. They are still the candidate's, to be given up or tried again.</summary>
        private static void Withdraw(PhysicsOwnerCandidate candidate)
        {
            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide side = candidate.Side(positive);
                if (side?.Root != null)
                {
                    side.Root.SetActive(false);
                }
            }
        }
    }
}
