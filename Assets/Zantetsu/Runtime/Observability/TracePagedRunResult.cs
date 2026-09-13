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

        private TracePagedRunResult(
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

        /// <summary>
        /// Ends one Run's variable-length trace and says what it came to: the
        /// last records come out of the lanes, the history is asked what it
        /// holds, and the Run is judged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The producers having stopped - every job finished, every worker
        /// stopped, every writer done with - is the caller's promise, exactly
        /// as it was for the final drain. Nothing here proves it again, owns
        /// or releases the lanes or the history, or keeps anything of its own
        /// between calls. Finishing twice is refused by the drainer's own
        /// seal, which is where that rule lives.
        /// </para>
        /// <para>
        /// A failure on the way through comes back as it is and no result is
        /// made: nothing is rolled back, nothing is retried, and nothing is
        /// written into the history to say what happened, which would itself
        /// be a record needing room.
        /// </para>
        /// </remarks>
        internal static TracePagedRunResult Finish(
            TraceLaneDrainer drainer, TracePagedHistory history)
        {
            if (drainer == null)
            {
                throw new ArgumentNullException(nameof(drainer));
            }

            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            TraceFinalDrainResult finalDrain = drainer.DrainToEndAndSeal(history);

            long committed = history.CommittedRecordCount;
            long dropped = history.DropCount;
            TracePagedHistoryView view = history.CreateView();

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
