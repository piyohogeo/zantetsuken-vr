using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// What one Run's variable-length trace came to: what the last drain took,
    /// what the history ended up holding, whether anything was lost, and a way
    /// of reading the history back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two record counts are not the same kind of figure.
    /// <see cref="FinalDrain"/>'s record count is what the final drain itself
    /// took out of the lanes - the ordinary drains that ran earlier in the Run
    /// are not in it. <see cref="HistoryCommittedRecordCount"/> is everything
    /// the history holds, ordinary drains included.
    /// </para>
    /// <para>
    /// The two drop counts stay apart as well: one is the records the lanes
    /// could not take from their producers, the other the records the history
    /// had no room for. Nothing here adds them together or gives a reason for
    /// either.
    /// </para>
    /// </remarks>
    internal readonly struct TracePagedRunResult
    {
        private readonly TraceFinalDrainResult _finalDrain;
        private readonly long _historyCommittedRecordCount;
        private readonly long _historyDropCount;
        private readonly TraceIntegrityState _integrity;
        private readonly TracePagedHistoryView _view;

        internal TracePagedRunResult(
            TraceFinalDrainResult finalDrain,
            long historyCommittedRecordCount,
            long historyDropCount,
            TraceIntegrityState integrity,
            TracePagedHistoryView view)
        {
            _finalDrain = finalDrain;
            _historyCommittedRecordCount = historyCommittedRecordCount;
            _historyDropCount = historyDropCount;
            _integrity = integrity;
            _view = view;
        }

        /// <summary>
        /// What the final drain took and what the lanes had dropped. Its record
        /// count covers this drain alone, not the whole Run.
        /// </summary>
        internal TraceFinalDrainResult FinalDrain => _finalDrain;

        /// <summary>
        /// Every record the history holds, from the ordinary drains as well as
        /// the final one.
        /// </summary>
        internal long HistoryCommittedRecordCount => _historyCommittedRecordCount;

        /// <summary>Every record the history had no room for, over the whole Run.</summary>
        internal long HistoryDropCount => _historyDropCount;

        /// <summary>
        /// Whether anything was lost: <see cref="TraceIntegrityState.Complete"/>
        /// only when neither the lanes nor the history dropped a record.
        /// </summary>
        internal TraceIntegrityState Integrity => _integrity;

        /// <summary>
        /// Reads the history back, now that nothing is writing to it. It is
        /// only good for as long as the history itself is.
        /// </summary>
        internal TracePagedHistoryView View => _view;
    }

    /// <summary>
    /// Ends one Run's variable-length trace: takes the last records out of the
    /// lanes, sees what the history came to, and says whether anything was
    /// lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds the drainer and the history it was given and nothing else. The
    /// producers having stopped is the caller's promise, as it already was for
    /// the final drain, and nothing here proves it again or keeps a registry,
    /// lease, receipt, generation, latch, or state of its own to say so.
    /// </para>
    /// <para>
    /// Finishing twice is refused by the drainer's own seal, which is where
    /// that rule lives; this type adds no second one. A failure on the way
    /// through - a destination that throws, a history already released - comes
    /// back as it is, and no result is handed out: there is nothing to roll
    /// back, nothing is retried, and nothing is written into the history to say
    /// what happened, which would be a record needing room of its own.
    /// </para>
    /// </remarks>
    internal sealed class TracePagedRunFinalizer
    {
        private readonly TraceLaneDrainer _drainer;
        private readonly TracePagedHistory _history;

        internal TracePagedRunFinalizer(TraceLaneDrainer drainer, TracePagedHistory history)
        {
            _drainer = drainer ?? throw new ArgumentNullException(nameof(drainer));
            _history = history ?? throw new ArgumentNullException(nameof(history));
        }

        /// <summary>
        /// Drains what is left into the history, reads what the history came
        /// to, and returns the Run's trace result.
        /// </summary>
        internal TracePagedRunResult Finish()
        {
            TraceFinalDrainResult finalDrain = _drainer.DrainToEndAndSeal(_history);

            long committed = _history.CommittedRecordCount;
            long dropped = _history.DropCount;
            TracePagedHistoryView view = _history.CreateView();

            // A Run is complete only if nothing was lost on either side: a
            // producer the lane had no room for, or a record the history had no
            // room for. Which of the two it was stays visible in the counts.
            TraceIntegrityState integrity = finalDrain.DropCount == 0L && dropped == 0L
                ? TraceIntegrityState.Complete
                : TraceIntegrityState.Incomplete;

            return new TracePagedRunResult(finalDrain, committed, dropped, integrity, view);
        }
    }
}
