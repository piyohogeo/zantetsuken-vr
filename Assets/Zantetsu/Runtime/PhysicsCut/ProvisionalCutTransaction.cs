using System;
using System.Collections.Generic;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How far one accepted cut has come on the physics side, as the driver of it sees it.</summary>
    public enum ProvisionalCutPhase
    {
        /// <summary>Accepted, classified, and nothing of it built yet.</summary>
        Accepted = 0,

        /// <summary>The Provisional pair is in the physics scene in place of the source.</summary>
        Published = 1,

        /// <summary>The final numbers and meshes are in hand, waiting for a handoff that is not written yet.</summary>
        FinalHeld = 2,

        /// <summary>
        /// Accepted by the ledger and **not established**: the resources this record had made have gone back, but the
        /// cut is still the ledger's active operation and still holds a unit of its incomplete budget. The record is
        /// kept for that reason alone -- so that the ending entrance can close what the ledger still has.
        /// </summary>
        Unestablished = 3,

        /// <summary>Over: everything this held has gone back, once.</summary>
        Recovered = 4,
    }

    /// <summary>
    /// What one accepted cut owns on the physics side, from the moment it is accepted until everything it took has
    /// gone back: the classification the kernel reads, the hold on the input shape, the cut request, the unpublished
    /// pair, and the final products.
    /// <para>
    /// **Why one record.** Each of those is given back at a different moment and each exactly once, and the moments
    /// are not the same as any one stage: a request can end while the products it made are still held, a pair can be
    /// published while the cut is still running, and the hold on the input has to outlive both. Holding them apart
    /// per cut is what makes "once, and at the right time" something a reader can check.
    /// </para>
    /// <para>
    /// **The hold on the input shape is the subtle one.** A finished cut's products name borrowed convexes, which are
    /// ranges in the **input** shape's bank and meshes the input shape holds. Whoever later builds a final side reads
    /// them from there (<see cref="PhysicsOwnerShape.OfSide"/>). So the hold is taken when the cut is submitted and
    /// let go when this record stops holding the products -- not when the numerical job finished, not when the
    /// request ended, and not because a publication is still waiting. The source's own owner may be retired long
    /// before that; the hold is what keeps the bank and the meshes alive across it.
    /// </para>
    /// <para>
    /// This owns no ledger state and no correspondence. Ending a published pair and retiring a source are the
    /// registry's (<see cref="PhysicsOwnerRegistry.EndProvisional"/>, <see cref="PhysicsOwnerRegistry.Retire"/>), and
    /// what the ledger does with the cut is the ledger's.
    /// </para>
    /// </summary>
    public sealed class ProvisionalCutTransaction
    {
        private PhysicsOwnerShape _inputShape;
        private bool _holdsInput;

        internal ProvisionalCutTransaction(
            CutOperationId operation, LogicalFragmentId source, PhysicsCutClassification classification)
        {
            Operation = operation;
            Source = source;
            Classification = classification ?? throw new ArgumentNullException(nameof(classification));
        }

        /// <summary>The accepted cut this is of.</summary>
        public CutOperationId Operation { get; }

        /// <summary>The fragment it is a cut of.</summary>
        public LogicalFragmentId Source { get; }

        public ProvisionalCutPhase Phase { get; private set; } = ProvisionalCutPhase.Accepted;

        /// <summary>The one robust-support scan, read by the pair and by the kernel alike.</summary>
        public PhysicsCutClassification Classification { get; private set; }

        /// <summary>The unpublished pair, while it is unpublished. Null once it is published or given up.</summary>
        public ProvisionalOwnerCandidate Candidate { get; private set; }

        /// <summary>The published pair, once there is one. The registry is what ends it.</summary>
        public ProvisionalOwnerPair Pair { get; private set; }

        /// <summary>The cut request, while it is running. Null before it is submitted and after it has ended.</summary>
        public PhysicsCutRequest Cut { get; private set; }

        /// <summary>
        /// The final numbers and meshes of the cut, kept because a handoff will need them. They are **not** given up
        /// for want of a handoff: holding them is what this phase is.
        /// </summary>
        public PhysicsCutProducts Products { get; private set; }

        /// <summary>How the cut ended, once it has. Pending while it is running or was never submitted.</summary>
        public PhysicsCutOutcomeKind CutOutcome { get; private set; } = PhysicsCutOutcomeKind.Pending;

        /// <summary>
        /// This cut has been asked to end -- an abort, a stale result, a teardown -- and everything it holds goes back
        /// as soon as it may. **Asking is not collecting**: while a submitted work has not come back, the arrays the
        /// kernel is pointing at and the input it borrows are still needed, so they stay. Nothing is waited for and
        /// nothing is completed by force; <see cref="TryFinishIfCollected"/> is what ends it when the work returns.
        /// </summary>
        public bool IsEnding { get; private set; }

        /// <summary>The frame the pair was published in, as the caller counted frames. Zero until it is published.</summary>
        public int PublishedFrame { get; private set; }

        /// <summary>Whether the hold on the input shape's bank and meshes is still taken.</summary>
        public bool HoldsInput => _holdsInput;

        /// <summary>The shape the cut reads, while this holds it.</summary>
        public PhysicsOwnerShape InputShape => _inputShape;

        /// <summary>
        /// What was asked for: the plane, the two impulses and the render anchor. It is kept because the steps after
        /// the acceptance read it — the plane the pair is allocated by, the impulses each side is given, the anchor the
        /// first velocity is about — and because it is what this record was accepted for, which is worth being able to
        /// read afterwards. Nothing takes a cut up again from here: there is no resumption.
        /// </summary>
        public ProvisionalCutAsk Ask { get; private set; }

        internal void Asked(in ProvisionalCutAsk ask)
        {
            Ask = ask;
        }

        internal void Took(ProvisionalOwnerCandidate candidate)
        {
            Candidate = candidate;
        }

        /// <summary>The pair is in the scene: the candidate has handed its actors over and names them no more.</summary>
        internal void Published(ProvisionalOwnerPair pair, int frame)
        {
            Pair = pair;
            Candidate = null;
            PublishedFrame = frame;
            Phase = ProvisionalCutPhase.Published;
        }

        /// <summary>
        /// Asked to end. What can go back now does; what a submitted work still needs stays until that work is
        /// collected.
        /// </summary>
        internal void Ending()
        {
            IsEnding = true;
        }

        /// <summary>
        /// Gives back what this record made and could not use -- the unpublished pair and the classification -- while
        /// **keeping the record itself**, because the cut it is of is still the ledger's active operation.
        /// <para>
        /// This is the difference between two things that look alike: the resources are not needed any more, and the
        /// cut is still there to be ended. Reading the first as the second is what loses an acceptance: the ledger goes
        /// on holding an active operation and a unit of its incomplete budget, the next cut of that fragment is refused
        /// for it, and nobody is left holding anything that could end it.
        /// </para>
        /// <para>
        /// Ending it is an explicit ending, through whoever holds this record; nothing here reads a refusal as an abort
        /// and nothing retries by itself.
        /// </para>
        /// </summary>
        internal void GiveUpUnpublished()
        {
            if (Phase == ProvisionalCutPhase.Recovered || Phase == ProvisionalCutPhase.Unestablished)
            {
                return;
            }

            Candidate?.Dispose();
            Candidate = null;
            Classification?.Dispose();
            Classification = null;
            Phase = ProvisionalCutPhase.Unestablished;
        }

        /// <summary>
        /// Ends this record if nothing is outstanding any more: false while a submitted work has not come back, true
        /// once everything it held has gone back. It waits for nothing and completes nothing -- it looks, and acts if
        /// it may. A record that was never asked to end is left alone.
        /// <para>
        /// This is what whoever drives the cut and the frame calls: the record outlives the thing that made it, because
        /// the work does.
        /// </para>
        /// </summary>
        public bool TryFinishIfCollected()
        {
            if (Phase == ProvisionalCutPhase.Recovered)
            {
                return true;
            }

            if (Cut != null && !Cut.IsOver)
            {
                return false;
            }

            if (Cut != null)
            {
                CutEnded(Cut.Outcome, Cut.Products);
            }

            if (!IsEnding)
            {
                return false;
            }

            Recover();
            return true;
        }

        /// <summary>
        /// The cut has been submitted, and with it the hold that keeps the input's bank and meshes alive for as long
        /// as anything can still read the borrowed parts of what it produces.
        /// </summary>
        internal void Submitted(PhysicsCutRequest request, PhysicsOwnerShape inputShape)
        {
            Cut = request;
            _inputShape = inputShape;
            inputShape.AcquireForWork();
            _holdsInput = true;
        }

        /// <summary>
        /// The cut ended. Its products, if it made any, are kept here; the classification the kernel was pointing at
        /// is given back, because nothing reads it once the request is over. **The hold on the input stays**: the
        /// products' borrowed parts are ranges in that input's bank.
        /// </summary>
        internal void CutEnded(PhysicsCutOutcomeKind outcome, PhysicsCutProducts products)
        {
            Cut = null;
            CutOutcome = outcome;
            Products = products;
            Classification?.Dispose();
            Classification = null;
            if (products != null && Phase != ProvisionalCutPhase.Recovered)
            {
                Phase = ProvisionalCutPhase.FinalHeld;
            }
        }

        /// <summary>
        /// Gives back everything this still holds, once: an unpublished pair is destroyed, the final products are
        /// given up, the classification goes back, and the hold on the input is let go **last**, because the products
        /// were reading through it.
        /// <para>
        /// What it does not do: end a published pair, retire the source, or tell the ledger anything. Those belong to
        /// the registry and the ledger and are the caller's to do in the order it decides. A cut whose request is
        /// still with the dispatcher is not interrupted here either -- the caller abandons it and recovers when it
        /// comes back.
        /// </para>
        /// </summary>
        internal void Recover()
        {
            if (Phase == ProvisionalCutPhase.Recovered)
            {
                return;
            }

            Phase = ProvisionalCutPhase.Recovered;
            Candidate?.Dispose();
            Candidate = null;
            Classification?.Dispose();
            Classification = null;
            Products?.Dispose();
            Products = null;

            if (_holdsInput)
            {
                _holdsInput = false;
                _inputShape.ReleaseFromWork();
                _inputShape = null;
            }
        }
    }

    /// <summary>
    /// The records that have been asked to end and are waiting for their work to come back, carried to the end.
    /// <para>
    /// **It is a participant of the shared frame** (<see cref="IMainThreadPump"/>), which is what makes this a product
    /// path rather than an explanation: the frame is pumped by whoever owns it, and it outlives the driver that made
    /// these records -- so a cut whose work was still out when its driver went away is still finished, by the same
    /// pumping that collects that work. Nothing here waits, sleeps or completes anything: each round it asks each
    /// record whether its work has come back, and lets go of the ones that are done.
    /// </para>
    /// <para>
    /// It holds nothing else and decides nothing. The ledger, the pair and the source have already been dealt with by
    /// whoever asked the record to end; what is left here is the record's own resources.
    /// </para>
    /// </summary>
    public sealed class ProvisionalCutRecovery : IMainThreadPump
    {
        private readonly List<ProvisionalCutTransaction> _ending = new List<ProvisionalCutTransaction>(4);

        /// <summary>How many records are still waiting for their work to come back.</summary>
        public int Count => _ending.Count;

        /// <summary>
        /// Takes one record that has been asked to end and could not finish at once. A record that is already finished
        /// is not kept, and one that is already here is not kept twice.
        /// </summary>
        public void Keep(ProvisionalCutTransaction transaction)
        {
            if (transaction == null || transaction.Phase == ProvisionalCutPhase.Recovered)
            {
                return;
            }

            if (!_ending.Contains(transaction))
            {
                _ending.Add(transaction);
            }
        }

        /// <summary>
        /// Asks each record whether its work has come back, and ends the ones that may. True when something really
        /// ended, which is what the frame counts as progress.
        /// </summary>
        public bool Pump()
        {
            bool moved = false;
            for (int i = _ending.Count - 1; i >= 0; i--)
            {
                if (!_ending[i].TryFinishIfCollected())
                {
                    continue;
                }

                _ending.RemoveAt(i);
                moved = true;
            }

            return moved;
        }
    }
}
