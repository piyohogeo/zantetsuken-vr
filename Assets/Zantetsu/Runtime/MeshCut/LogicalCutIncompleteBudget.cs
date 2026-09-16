using System;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// DESIGN 7.7's one limit on admitted-and-not-yet-completed cut operations, counted **over every target** — which
    /// is why it is not a field of a ledger: every <see cref="LogicalCutLedger"/> that shares this object shares the
    /// same count, so one object filling it leaves nothing for the others. It is a plain counter with a ceiling and
    /// nothing else: no queue, no scheduler, no profile, no reservation ahead of time.
    /// <para>
    /// A ledger takes one unit when a request passes admission and gives it back exactly once when that operation
    /// reaches a terminal state — completion, termination, abort or stale reclamation. Logical publication gives
    /// nothing back, because the operation's geometry responsibility is still open (7.7: Pendingは自分のGeometry
    /// CommitまたはBranch退役・Stale終端まで残り、一度だけ減算する).
    /// </para>
    /// </summary>
    public sealed class LogicalCutIncompleteBudget
    {
        /// <param name="maxIncompleteCutOperationCount">
        /// A fixed positive value. The product value is decided after measurement; a test states a small one.
        /// </param>
        public LogicalCutIncompleteBudget(int maxIncompleteCutOperationCount)
        {
            if (maxIncompleteCutOperationCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxIncompleteCutOperationCount), "must be positive");
            }

            MaxIncompleteCutOperationCount = maxIncompleteCutOperationCount;
        }

        public int MaxIncompleteCutOperationCount { get; }

        /// <summary>Operations admitted and not yet ended, over every ledger sharing this. Never exceeds the limit.</summary>
        public int IncompleteCutOperationCount { get; private set; }

        public bool IsFull => IncompleteCutOperationCount >= MaxIncompleteCutOperationCount;

        // Called by a ledger only after it has checked IsFull as part of admission; a full budget here is a ledger bug.
        internal void Take()
        {
            if (IsFull)
            {
                throw new InvalidOperationException("the incomplete budget is full");
            }

            IncompleteCutOperationCount++;
        }

        // Called by a ledger once per operation, when the operation reaches a terminal state.
        internal void Return()
        {
            if (IncompleteCutOperationCount <= 0)
            {
                throw new InvalidOperationException("nothing is taken from the incomplete budget");
            }

            IncompleteCutOperationCount--;
        }
    }
}
