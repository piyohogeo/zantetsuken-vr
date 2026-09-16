using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The logical admission and publication of cuts for one object, as pure data (DESIGN 4.2, 7.1, 7.7, 8; Phase 1's
    /// first part, DESIGN 15 line "正負の確定子とOperationの原子的な公開を純粋データで確認する"). It holds the
    /// fragments and the operations of its object, and nothing else: no geometry, no storage, no display, no job, no
    /// physics. The incomplete count is not its own either — it is the <see cref="LogicalCutIncompleteBudget"/> the
    /// ledger was given, shared by every object's ledger, because DESIGN 7.7's limit is over all targets. What the
    /// physics side eventually says — established on both sides, or not — reaches the ledger through
    /// <see cref="Publish"/> and <see cref="Abort"/>; in Phases 1–3 that is a harness's synthetic outcome, and later the
    /// real one takes the same two entrances.
    /// <para>
    /// **Admission** (<see cref="Admit"/>) checks in the specified order — the source is live, it has no active
    /// operation, the request was classified as splitting both sides, and the shared budget has room — and only a
    /// request that passes all four is given a <see cref="CutOperationId"/> and takes one unit of the budget. A skip
    /// changes nothing, is not stored, and is not retried. Admission takes no physics outcome: the classification given
    /// here is the pre-admission snapshot, and what final physics later makes of the cut is a separate matter.
    /// </para>
    /// <para>
    /// **Publication** puts the two children and the operation in place together, within one call on the main thread,
    /// so a reader sees either the whole old state or the whole new one. Everything that could fail — an id running
    /// out, the list needing to grow — is done before the first change; the changes themselves are then assignments and
    /// additions into room already reserved. The parent stops being a current target. The budget is **not** touched by
    /// publication: the operation's geometry responsibility is still open, and its unit comes back exactly once, later,
    /// through <see cref="CompleteGeometry"/> or <see cref="Terminate"/>. An Abort retires the source; a result that
    /// finds its source's ownership authority changed is Stale and retires nothing. A duplicate or late result for an
    /// operation that is no longer active is refused with nothing changed.
    /// </para>
    /// <para>
    /// Authority is per source, not per object: a fragment's ownership authority changes only through
    /// <see cref="NoteOwnershipChanged"/> on that fragment, so another fragment's cut or change is never a reason to
    /// reject this one (DESIGN 8: 別LogicalFragmentだけの更新を通常切断の単独Reject理由にしない). Identities are
    /// never reused, and an id that is unset, or was never issued here, names nothing and changes nothing at any
    /// entrance.
    /// </para>
    /// <para>
    /// Not here: anchors, dispatch, jobs, clip/stencil, temporary display, ancestor-ordered geometry commit, boundary
    /// records and stale reclamation of geometry itself. <see cref="CompleteGeometry"/> is only the notice that gives
    /// the budget unit back; it is not a commit.
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
        }

        private struct Operation
        {
            public LogicalFragmentId source;
            public float4 plane;
            public int sourceAuthorityAtAdmission;
            public LogicalCutOperationState state;
            public LogicalFragmentId positive;
            public LogicalFragmentId negative;
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

        /// <summary>Adds a live fragment with no parent: a registered object before any cut.</summary>
        public LogicalFragmentId AddFragment()
        {
            ReserveFragments(1);
            return NewFragment();
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
        /// Asks for a cut of <paramref name="source"/> by <paramref name="plane"/>. The checks run in DESIGN 4.2's
        /// order and stop at the first that fails; only <see cref="LogicalCutAdmission.Admitted"/> issues an id, makes
        /// the operation the source's active one, and takes one unit of the shared budget. Every other outcome leaves
        /// the ledger and the budget exactly as they were and keeps nothing of the request.
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
        /// Final physics established on both sides (DESIGN 7.1.2): publishes the positive and negative child and the
        /// operation together, and ends the source as a current target. The budget does not change. Refused as
        /// <see cref="LogicalCutResultOutcome.Stale"/> if the source's authority changed since admission, and as
        /// <see cref="LogicalCutResultOutcome.NotActive"/> if the operation is not the source's active one any more.
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

            // Preparation: the only things that can fail — two more ids being representable, and the list having room
            // for two more — are settled here, before the first change. If this throws, nothing has moved.
            ReserveFragments(2);

            // Publication: additions into reserved room and assignments, nothing that can fail part-way, so a reader
            // on this thread sees the whole old state before this call and the whole new state after it.
            positive = NewFragment();
            negative = NewFragment();

            Operation operation = _operations[operationIndex];
            operation.state = LogicalCutOperationState.Published;
            operation.positive = positive;
            operation.negative = negative;
            _operations[operationIndex] = operation;

            Fragment source = _fragments[sourceIndex];
            source.state = LogicalFragmentState.Replaced;
            source.activeOperation = default;
            _fragments[sourceIndex] = source;

            return LogicalCutResultOutcome.Applied;
        }

        /// <summary>
        /// Final physics could not be established before publication (DESIGN 7.1.3): the operation ends, the source is
        /// retired, and the budget unit comes back once. Nothing is published for either side. Waiting alone is never
        /// a reason to call this. Stale and not-active results are refused exactly as for <see cref="Publish"/>.
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
            _fragments[sourceIndex] = source;

            return LogicalCutResultOutcome.Applied;
        }

        /// <summary>
        /// The notice that a published operation's geometry responsibility is done. It gives the budget unit back,
        /// once. This is not a geometry commit: nothing about geometry, frames, boundaries or temporary display lives
        /// here.
        /// </summary>
        public LogicalCutResultOutcome CompleteGeometry(CutOperationId id)
        {
            return EndPublished(id, LogicalCutOperationState.Completed);
        }

        /// <summary>
        /// The notice that a published operation ended without its geometry completing — its branch retired, or its
        /// late geometry result reclaimed. It gives the budget unit back, once, exactly as completion does.
        /// </summary>
        public LogicalCutResultOutcome Terminate(CutOperationId id)
        {
            return EndPublished(id, LogicalCutOperationState.Terminated);
        }

        /// <summary>
        /// Records that something other than the fragment's own cut changed its ownership (DESIGN 8: 別Transaction・
        /// Maintenance・GC・外部SystemのOwner／Shape／Constraint構成変更). A result for an operation admitted before
        /// this arrives as Stale. The fragment itself stays as it is, and no other fragment is affected.
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
        // that happens once per operation because terminal states are never left.
        private void End(int operationIndex, LogicalCutOperationState terminal)
        {
            Operation operation = _operations[operationIndex];
            operation.state = terminal;
            _operations[operationIndex] = operation;
            Budget.Return();
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

        // Only after ReserveFragments: the addition goes into reserved room and does not grow the list.
        private LogicalFragmentId NewFragment()
        {
            int id = _fragments.Count + 1;
            _fragments.Add(new Fragment { state = LogicalFragmentState.Live });
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
