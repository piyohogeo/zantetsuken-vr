using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The logical admission and publication of cuts for one object, as pure data (DESIGN 4.2, 7.1, 7.7, 8; Phase 1's
    /// first part, DESIGN 15 line "正負の確定子とOperationの原子的な公開を純粋データで確認する"). It holds the
    /// fragments and the operations of its object, and each fragment's owner anchor set, and nothing else: no geometry,
    /// no storage, no display, no job, no physics. The incomplete count is not its own either — it is the
    /// <see cref="LogicalCutIncompleteBudget"/> the ledger was given, shared by every object's ledger, because DESIGN
    /// 7.7's limit is over all targets. What the physics side eventually says — established on both sides, or not —
    /// reaches the ledger through <see cref="Publish"/> and <see cref="Abort"/>; in Phases 1–3 that is a harness's
    /// synthetic outcome, and later the real one takes the same two entrances.
    /// <para>
    /// **A fragment here stands for an owner** in the pure-data sense of DESIGN 7.1 — the unit an anchor set belongs
    /// to and the unit whose fixity follows from it. It is **not** an actor: nothing here creates, poses or configures
    /// a physics body, and static/kinematic is Phase 4's to set.
    /// </para>
    /// <para>
    /// **Admission** (<see cref="Admit"/>) checks in the specified order — the source is live, it has no active
    /// operation, the request was classified as splitting both sides, and the shared budget has room — and only a
    /// request that passes all four is given a <see cref="CutOperationId"/> and takes one unit of the budget. A skip
    /// changes nothing, is not stored, and is not retried. Admission takes no physics outcome: the classification given
    /// here is the pre-admission snapshot, and what final physics later makes of the cut is a separate matter.
    /// </para>
    /// <para>
    /// **Anchors** are registered with the fragment that owns them (<see cref="AddFragment(IReadOnlyList{float3})"/>)
    /// and are then the ledger's own copy: a caller may keep and change its own list freely, and a query hands back a
    /// copy rather than the ledger's list. There is no API for editing a registered set. After a cut is admitted,
    /// <see cref="PrepareAnchorDistribution"/> distributes the source's current set across the operation's own adopted
    /// plane, using <see cref="FixedSupportAnchors"/> — the classification lives there and is not repeated here. The
    /// prepared sets stay unpublished until the children are.
    /// <para>
    /// A source **lets its own set go** once it is no longer a current target: replaced by its children, or retired by
    /// an Abort. The children hold what they inherited, and nothing keeps an ancestor's points alive for the sake of
    /// history. A source that stays live — one whose result turned out Stale — keeps its set, because it can still be
    /// cut again.
    /// </para>
    /// </para>
    /// <para>
    /// **Publication** requires that distribution to have been prepared, and then puts the two children, the operation
    /// and each child's anchor set in place together, within one call on the main thread, so a reader sees either the
    /// whole old state or the whole new one. Everything that could fail — an id running out, the list needing to grow —
    /// is done before the first change; the children then take the prepared sets as they are, with nothing
    /// reclassified or copied again. The parent stops being a current target. An unprepared operation is refused as
    /// <see cref="LogicalCutResultOutcome.AnchorsNotPrepared"/> with nothing changed: a distribution that was never
    /// prepared must never be read as "this owner had no anchors". The budget is **not** touched by publication: the
    /// operation's geometry responsibility is still open, and its unit comes back exactly once, later, through
    /// <see cref="CompleteGeometry"/> or <see cref="Terminate"/>. An Abort retires the source; a result that finds its
    /// source's ownership authority changed is Stale and retires nothing. Either way the prepared sets are dropped
    /// unpublished. A duplicate or late result for an operation that is no longer active is refused with nothing
    /// changed.
    /// </para>
    /// <para>
    /// Authority is per source, not per object: a fragment's ownership authority changes only through
    /// <see cref="NoteOwnershipChanged"/> on that fragment, so another fragment's cut or change is never a reason to
    /// reject this one (DESIGN 8: 別LogicalFragmentだけの更新を通常切断の単独Reject理由にしない). Identities are
    /// never reused, and an id that is unset, or was never issued here, names nothing and changes nothing at any
    /// entrance.
    /// </para>
    /// <para>
    /// Not here: dispatch, jobs, clip/stencil, temporary display, ancestor-ordered geometry commit, boundary records,
    /// stale reclamation of geometry itself, and every part of real physics. <see cref="CompleteGeometry"/> is only the
    /// notice that gives the budget unit back; it is not a commit.
    /// </para>
    /// </summary>
    public sealed class LogicalCutLedger
    {
        private struct Fragment
        {
            public LogicalFragmentState state;
            public CutOperationId activeOperation;

            // The source's ownership authority as a counter: bumped by NoteOwnershipChanged, snapshotted at admission,
            // and compared when the result arrives. Local to this fragment on purpose.
            public int authority;

            // This owner's anchor set, owned by the ledger. Null is an owner with no fixed support, which is the same
            // thing as an empty set and is perfectly normal.
            public List<float3> anchors;
        }

        private struct Operation
        {
            public LogicalFragmentId source;
            public float4 plane;
            public int sourceAuthorityAtAdmission;
            public LogicalCutOperationState state;
            public LogicalFragmentId positive;
            public LogicalFragmentId negative;

            // The anchor distribution, prepared after admission and handed to the children at publication. Once
            // prepared it is never redone, so a second preparation cannot overwrite it with another epsilon. The two
            // lists are dropped — handed on, or let go — the moment the operation leaves Admitted.
            public bool anchorsPrepared;
            public float anchorEpsilon;
            public AnchorDistributionResult anchorDistribution;
            public List<float3> preparedPositive;
            public List<float3> preparedNegative;
        }

        // Ids are list positions plus one, and nothing is ever removed, so an id is never reused.
        private readonly List<Fragment> _fragments = new List<Fragment>();
        private readonly List<Operation> _operations = new List<Operation>();

        /// <param name="budget">The incomplete-operation budget this object shares with every other (DESIGN 7.7).</param>
        public LogicalCutLedger(LogicalCutIncompleteBudget budget)
        {
            Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        /// <summary>The shared budget. Its count is over every ledger sharing it, not this object alone.</summary>
        public LogicalCutIncompleteBudget Budget { get; }

        /// <summary>How many fragment ids have been issued, ever.</summary>
        public int FragmentCount => _fragments.Count;

        /// <summary>How many operation ids have been issued, ever.</summary>
        public int OperationCount => _operations.Count;

        /// <summary>
        /// Adds a live fragment with no parent and no fixed support: a registered object, dynamic, before any cut.
        /// </summary>
        public LogicalFragmentId AddFragment()
        {
            return AddFragment(null);
        }

        /// <summary>
        /// Adds a live fragment with no parent, owning <paramref name="anchors"/> as its fixed support anchor set
        /// (DESIGN 7.1). The positions must be finite and are expressed in this owner's Fragment Physics Frame; null or
        /// empty means an owner with no fixed support. The set is **copied**, so the caller's own list may be kept and
        /// changed afterwards without affecting what was registered, and there is no API for editing it later.
        /// </summary>
        public LogicalFragmentId AddFragment(IReadOnlyList<float3> anchors)
        {
            List<float3> owned = CopyAnchors(anchors);
            ReserveFragments(1);
            return NewFragment(owned);
        }

        public bool TryGetFragmentState(LogicalFragmentId fragment, out LogicalFragmentState state)
        {
            if (!TryIndex(fragment, out int index))
            {
                state = default;
                return false;
            }

            state = _fragments[index].state;
            return true;
        }

        /// <summary>Whether the fragment is live: a target a cut can be admitted against.</summary>
        public bool IsCurrentTarget(LogicalFragmentId fragment)
        {
            return TryIndex(fragment, out int index) && _fragments[index].state == LogicalFragmentState.Live;
        }

        /// <summary>The fragment's active operation, if it has one.</summary>
        public bool TryGetActiveOperation(LogicalFragmentId fragment, out CutOperationId operation)
        {
            if (!TryIndex(fragment, out int index) || !_fragments[index].activeOperation.IsSet)
            {
                operation = default;
                return false;
            }

            operation = _fragments[index].activeOperation;
            return true;
        }

        public bool TryGetOperation(CutOperationId id, out LogicalCutOperation operation)
        {
            if (!TryIndex(id, out int index))
            {
                operation = default;
                return false;
            }

            Operation record = _operations[index];
            operation = new LogicalCutOperation(id, record.source, record.plane, record.state, record.positive, record.negative);
            return true;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with a **copy** of the fragment's anchor set, clearing it first, and returns
        /// false for an id this ledger never issued. The ledger's own list is never handed out, so what a caller does
        /// with the copy cannot change the registered set.
        /// </summary>
        public bool TryGetAnchors(LogicalFragmentId fragment, List<float3> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            if (!TryIndex(fragment, out int index))
            {
                return false;
            }

            into.Clear();
            List<float3> anchors = _fragments[index].anchors;
            if (anchors != null)
            {
                into.AddRange(anchors);
            }

            return true;
        }

        /// <summary>How many anchors the fragment owns, without copying them out.</summary>
        public bool TryGetAnchorCount(LogicalFragmentId fragment, out int count)
        {
            if (!TryIndex(fragment, out int index))
            {
                count = 0;
                return false;
            }

            count = _fragments[index].anchors?.Count ?? 0;
            return true;
        }

        /// <summary>
        /// Whether this owner is fixed: it holds at least one anchor (DESIGN 7.1). False for an owner with none, and
        /// false for an id this ledger never issued. Nothing else — no shape, no display geometry, no GPU state —
        /// takes part, and this is a logical judgement, not an actor being made static or kinematic.
        /// </summary>
        public bool IsFixedOwner(LogicalFragmentId fragment)
        {
            return TryGetAnchorCount(fragment, out int count) && FixedSupportAnchors.IsFixed(count);
        }

        /// <summary>
        /// Asks for a cut of <paramref name="source"/> by <paramref name="plane"/>. The checks run in DESIGN 4.2's
        /// order and stop at the first that fails; only <see cref="LogicalCutAdmission.Admitted"/> issues an id, makes
        /// the operation the source's active one, and takes one unit of the shared budget. Every other outcome leaves
        /// the ledger, the budget and every anchor set exactly as they were and keeps nothing of the request.
        /// </summary>
        /// <param name="splitsBothSides">
        /// The pre-admission classification: whether the cut is judged to leave something on both sides. False is a
        /// no-op (DESIGN 7.6). In Phase 1 this is the harness's synthetic classification; the robust-support
        /// classification of real convexes is Phase 4's, and takes this same place.
        /// </param>
        public LogicalCutAdmission Admit(LogicalFragmentId source, float4 plane, bool splitsBothSides, out CutOperationId operation)
        {
            if (!math.all(math.isfinite(plane)))
            {
                throw new ArgumentException("the adopted plane must be finite", nameof(plane));
            }

            operation = default;

            if (!TryIndex(source, out int sourceIndex) || _fragments[sourceIndex].state != LogicalFragmentState.Live)
            {
                return LogicalCutAdmission.SourceNotLive;
            }

            if (_fragments[sourceIndex].activeOperation.IsSet)
            {
                return LogicalCutAdmission.SourceActive;
            }

            if (!splitsBothSides)
            {
                return LogicalCutAdmission.NoOp;
            }

            if (Budget.IsFull)
            {
                return LogicalCutAdmission.Full;
            }

            // Passed. What can fail comes first, before anything is changed; then registration, the source's active
            // operation and the budget move together in this one call.
            ReserveOperations(1);

            Fragment fragment = _fragments[sourceIndex];
            operation = NewOperation(new Operation
            {
                source = source,
                plane = plane,
                sourceAuthorityAtAdmission = fragment.authority,
                state = LogicalCutOperationState.Admitted,
            });
            fragment.activeOperation = operation;
            _fragments[sourceIndex] = fragment;
            Budget.Take();
            return LogicalCutAdmission.Admitted;
        }

        /// <summary>
        /// Distributes the source owner's current anchor set across the operation's own adopted plane (DESIGN 7.1),
        /// leaving the result prepared and unpublished. Only an admitted operation can be prepared, and the source and
        /// the plane are the ones the operation already holds — neither is passed in, so no other plane and no
        /// arbitrary child set can be substituted at publication time.
        /// <para>
        /// The <paramref name="anchorEpsilon"/> is settled here. Preparing an operation that is **already** prepared
        /// changes nothing and hands back the distribution it already has, so a later call with a different epsilon
        /// cannot overwrite a successful preparation.
        /// </para>
        /// <para>
        /// An owner with no anchors prepares normally, as a distribution with both sides empty — which is what makes
        /// "prepared and empty" distinguishable from "never prepared". A refused distribution
        /// (<see cref="AnchorPreparationOutcome.DistributionRefused"/>) leaves the operation unprepared and changes
        /// nothing else: the source is **not** retired for it, and the caller decides what to do. Infeasibility is
        /// still <see cref="Abort"/>'s to declare, and lost authority still <see cref="Publish"/>'s or
        /// <see cref="Abort"/>'s to find as Stale; waiting or a refused preparation is neither.
        /// </para>
        /// </summary>
        public AnchorPreparationOutcome PrepareAnchorDistribution(
            CutOperationId id, float anchorEpsilon, out AnchorDistributionResult distribution)
        {
            distribution = default;

            if (!TryIndex(id, out int operationIndex)
                || _operations[operationIndex].state != LogicalCutOperationState.Admitted)
            {
                return AnchorPreparationOutcome.OperationNotActive;
            }

            Operation operation = _operations[operationIndex];
            if (operation.anchorsPrepared)
            {
                distribution = operation.anchorDistribution;
                return AnchorPreparationOutcome.Prepared;
            }

            if (!TryIndex(operation.source, out int sourceIndex))
            {
                return AnchorPreparationOutcome.OperationNotActive;
            }

            var positive = new List<float3>();
            var negative = new List<float3>();
            if (!FixedSupportAnchors.TryDistribute(
                    _fragments[sourceIndex].anchors, operation.plane, anchorEpsilon, positive, negative, out AnchorDistributionResult result))
            {
                distribution = result;
                return AnchorPreparationOutcome.DistributionRefused;
            }

            operation.anchorsPrepared = true;
            operation.anchorEpsilon = anchorEpsilon;
            operation.anchorDistribution = result;
            operation.preparedPositive = positive;
            operation.preparedNegative = negative;
            _operations[operationIndex] = operation;

            distribution = result;
            return AnchorPreparationOutcome.Prepared;
        }

        /// <summary>
        /// What a prepared operation would hand to its children, while it is still waiting to be published. False for
        /// an operation with no prepared distribution, and false once the operation has left
        /// <see cref="LogicalCutOperationState.Admitted"/> — published, aborted, stale or ended — because the
        /// distribution is gone by then and this is not a record of the past.
        /// </summary>
        public bool TryGetPreparedAnchorDistribution(CutOperationId id, out AnchorDistributionResult distribution)
        {
            if (!TryIndex(id, out int index)
                || _operations[index].state != LogicalCutOperationState.Admitted
                || !_operations[index].anchorsPrepared)
            {
                distribution = default;
                return false;
            }

            distribution = _operations[index].anchorDistribution;
            return true;
        }

        /// <summary>
        /// Final physics established on both sides (DESIGN 7.1.2): publishes the positive and negative child, the
        /// operation, and each child's inherited anchor set, all together, and ends the source as a current target.
        /// The budget does not change. Refused as <see cref="LogicalCutResultOutcome.Stale"/> if the source's
        /// authority changed since admission, as <see cref="LogicalCutResultOutcome.NotActive"/> if the operation is
        /// not the source's active one any more, and as
        /// <see cref="LogicalCutResultOutcome.AnchorsNotPrepared"/> if its distribution was never prepared — the last
        /// of these changes nothing at all, so an unprepared cut can still be prepared and published afterwards.
        /// </summary>
        public LogicalCutResultOutcome Publish(CutOperationId id, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            positive = default;
            negative = default;

            LogicalCutResultOutcome check = CheckActive(id, out int operationIndex, out int sourceIndex);
            if (check != LogicalCutResultOutcome.Applied)
            {
                return check;
            }

            Operation operation = _operations[operationIndex];
            if (!operation.anchorsPrepared)
            {
                // Never read an unprepared distribution as "no anchors": nothing is published and nothing changes.
                return LogicalCutResultOutcome.AnchorsNotPrepared;
            }

            // Preparation: the only things that can fail — two more ids being representable, and the list having room
            // for two more — are settled here, before the first change. If this throws, nothing has moved.
            ReserveFragments(2);

            // Publication: the children take the prepared sets as they are, with nothing reclassified or copied again.
            positive = NewFragment(operation.preparedPositive);
            negative = NewFragment(operation.preparedNegative);

            operation.state = LogicalCutOperationState.Published;
            operation.positive = positive;
            operation.negative = negative;

            // The operation lets the sets go now that the children hold them; the children's own references stand, so
            // a later completion or termination cannot take their anchors away.
            operation.preparedPositive = null;
            operation.preparedNegative = null;
            _operations[operationIndex] = operation;

            Fragment source = _fragments[sourceIndex];
            source.state = LogicalFragmentState.Replaced;
            source.activeOperation = default;

            // The children hold what they inherited, so the replaced source lets its own set go rather than keeping it
            // for the lifetime of the ledger. The list itself is not cleared — the children's sets are separate lists.
            source.anchors = null;
            _fragments[sourceIndex] = source;

            return LogicalCutResultOutcome.Applied;
        }

        /// <summary>
        /// Final physics could not be established before publication (DESIGN 7.1.3): the operation ends, the source is
        /// retired, its prepared distribution is dropped unpublished, and the budget unit comes back once. Nothing is
        /// published for either side. Waiting alone, and a refused preparation, are never reasons to call this. Stale
        /// and not-active results are refused exactly as for <see cref="Publish"/>.
        /// </summary>
        public LogicalCutResultOutcome Abort(CutOperationId id)
        {
            LogicalCutResultOutcome check = CheckActive(id, out int operationIndex, out int sourceIndex);
            if (check != LogicalCutResultOutcome.Applied)
            {
                return check;
            }

            End(operationIndex, LogicalCutOperationState.Aborted);

            Fragment source = _fragments[sourceIndex];
            source.state = LogicalFragmentState.Retired;
            source.activeOperation = default;

            // A retired source is nothing's target any more, so it lets its set go as well. Nothing inherited it.
            source.anchors = null;
            _fragments[sourceIndex] = source;

            return LogicalCutResultOutcome.Applied;
        }

        /// <summary>
        /// The notice that a published operation's geometry responsibility is done. It gives the budget unit back,
        /// once, and leaves the children and their anchor sets exactly as they are. This is not a geometry commit:
        /// nothing about geometry, frames, boundaries or temporary display lives here.
        /// </summary>
        public LogicalCutResultOutcome CompleteGeometry(CutOperationId id)
        {
            return EndPublished(id, LogicalCutOperationState.Completed);
        }

        /// <summary>
        /// The notice that a published operation ended without its geometry completing — its branch retired, or its
        /// late geometry result reclaimed. It gives the budget unit back, once, exactly as completion does, and takes
        /// nothing away from the children.
        /// </summary>
        public LogicalCutResultOutcome Terminate(CutOperationId id)
        {
            return EndPublished(id, LogicalCutOperationState.Terminated);
        }

        /// <summary>
        /// Records that something other than the fragment's own cut changed its ownership (DESIGN 8: 別Transaction・
        /// Maintenance・GC・外部SystemのOwner／Shape／Constraint構成変更). A result for an operation admitted before
        /// this arrives as Stale. The fragment itself stays as it is, anchors included, and no other fragment is
        /// affected.
        /// </summary>
        public void NoteOwnershipChanged(LogicalFragmentId fragment)
        {
            if (!TryIndex(fragment, out int index))
            {
                throw new ArgumentException("not a fragment of this ledger", nameof(fragment));
            }

            Fragment record = _fragments[index];
            record.authority = checked(record.authority + 1);
            _fragments[index] = record;
        }

        // The authority check every result goes through (DESIGN 8: Source生存性、SourceのActive CutOperationId、当該
        // Transactionが持つ…authorityで照合). Not active at all: refused with nothing changed. Active but the source's
        // authority moved: reclaimed as stale here, once, without touching the source.
        private LogicalCutResultOutcome CheckActive(CutOperationId id, out int operationIndex, out int sourceIndex)
        {
            sourceIndex = -1;
            if (!TryIndex(id, out operationIndex) || _operations[operationIndex].state != LogicalCutOperationState.Admitted)
            {
                return LogicalCutResultOutcome.NotActive;
            }

            Operation operation = _operations[operationIndex];
            if (!TryIndex(operation.source, out sourceIndex))
            {
                return LogicalCutResultOutcome.NotActive;
            }

            Fragment source = _fragments[sourceIndex];
            if (source.state != LogicalFragmentState.Live
                || source.activeOperation != id
                || source.authority != operation.sourceAuthorityAtAdmission)
            {
                End(operationIndex, LogicalCutOperationState.Stale);
                if (source.activeOperation == id)
                {
                    source.activeOperation = default;
                    _fragments[sourceIndex] = source;
                }

                return LogicalCutResultOutcome.Stale;
            }

            return LogicalCutResultOutcome.Applied;
        }

        private LogicalCutResultOutcome EndPublished(CutOperationId id, LogicalCutOperationState terminal)
        {
            if (!TryIndex(id, out int index) || _operations[index].state != LogicalCutOperationState.Published)
            {
                return LogicalCutResultOutcome.NotActive;
            }

            End(index, terminal);
            return LogicalCutResultOutcome.Applied;
        }

        // The one place a budget unit goes back: an operation leaves Admitted or Published for a terminal state, and
        // that happens once per operation because terminal states are never left. Any distribution still held here is
        // let go unpublished; what was already handed to children is theirs and is untouched.
        private void End(int operationIndex, LogicalCutOperationState terminal)
        {
            Operation operation = _operations[operationIndex];
            operation.state = terminal;
            operation.preparedPositive = null;
            operation.preparedNegative = null;
            _operations[operationIndex] = operation;
            Budget.Return();
        }

        // Copies a caller's anchor positions into the ledger's own list, refusing a non-finite position the way an
        // adopted plane is refused: it is the caller's mistake, not a state this ledger can hold.
        private static List<float3> CopyAnchors(IReadOnlyList<float3> anchors)
        {
            int count = anchors?.Count ?? 0;
            if (count == 0)
            {
                return null;
            }

            var owned = new List<float3>(count);
            for (int i = 0; i < count; i++)
            {
                float3 anchor = anchors[i];
                if (!math.all(math.isfinite(anchor)))
                {
                    throw new ArgumentException("an anchor position must be finite", nameof(anchors));
                }

                owned.Add(anchor);
            }

            return owned;
        }

        // Settles, before any change, that `count` more fragments can be issued: their ids stay representable and the
        // list has room for them without growing. Throws with nothing changed otherwise.
        private void ReserveFragments(int count)
        {
            int needed = checked(_fragments.Count + count);
            if (_fragments.Capacity < needed)
            {
                _fragments.Capacity = needed;
            }
        }

        private void ReserveOperations(int count)
        {
            int needed = checked(_operations.Count + count);
            if (_operations.Capacity < needed)
            {
                _operations.Capacity = needed;
            }
        }

        // Only after ReserveFragments: the addition goes into reserved room and does not grow the list. The anchor
        // list handed in becomes the fragment's own; callers pass either a fresh copy or a prepared distribution.
        private LogicalFragmentId NewFragment(List<float3> anchors)
        {
            int id = _fragments.Count + 1;
            _fragments.Add(new Fragment { state = LogicalFragmentState.Live, anchors = anchors });
            return new LogicalFragmentId(id);
        }

        // Only after ReserveOperations, likewise.
        private CutOperationId NewOperation(Operation record)
        {
            int id = _operations.Count + 1;
            _operations.Add(record);
            return new CutOperationId(id);
        }

        // An id names a record here only if it is positive and was issued by this ledger. Unset, and anything never
        // issued, names nothing; a negative value cannot exist (refused at construction).
        private bool TryIndex(LogicalFragmentId fragment, out int index)
        {
            index = fragment.value - 1;
            return fragment.IsSet && index < _fragments.Count;
        }

        private bool TryIndex(CutOperationId operation, out int index)
        {
            index = operation.value - 1;
            return operation.IsSet && index < _operations.Count;
        }
    }
}
