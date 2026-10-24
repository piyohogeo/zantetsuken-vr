using System.Threading;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The optional observation of a work's way through a destination (GRAPH-CONNECT-14 §2): a work that implements this is
    /// told, by a pool, the moment it was taken in. The pool calls it inside its accept lock and before the work is in its
    /// queue, so no worker can begin the work earlier than the stamp (t1 is at or before t2). The implementation must be a
    /// plain store: no wait, no allocation, no exception, no call back into the pool or the dispatcher.
    /// </summary>
    public interface IDispatchObserved
    {
        /// <summary>The acceptance's clock reading (<c>Stopwatch.GetTimestamp</c> ticks).</summary>
        void NoteAccepted(long timestamp);
    }

    /// <summary>
    /// The switch of the dispatch observation (development builds only; nothing of it exists in a Release Player): the acceptance
    /// stamp a pool gives an observed work, the workers' busy time, the dispatcher's counts by purpose and its capacity-blocked
    /// opportunities (a scan of the waiting queue). Off, none of these is read, counted or scanned; the existing counts
    /// (TotalSubmitted, TotalCollected, WaitingCount) are not these and stay.
    /// </summary>
    public static class DispatchObservation
    {
        /// <summary>Whether the observation is taken. On by default; <c>-zantetsuSplitTimelineOff</c> turns it off for a run.</summary>
        public static volatile bool Enabled = true;
    }
}
