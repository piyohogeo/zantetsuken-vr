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
    /// The optional name of a work in the work log (GRAPH-CONNECT-17 §3-E4): a number its own records know it by (the spatial
    /// split's attempt serial), read once when the work is offered. A plain read: no work, no allocation.
    /// </summary>
    public interface IDispatchLabeled
    {
        long DispatchLabel { get; }
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

        /// <summary>
        /// Whether every Background work's own way is kept (GRAPH-CONNECT-17 §3-E4, development builds): one record a work offered to
        /// the dispatcher for the Background pool -- its purpose and type, the enqueue (t0), the pool's acceptance (t1, the stamp it
        /// takes inside its lock), the worker's begin and end (t2, t3), the collection (t4), how often a dispatch opportunity could not submit it for want of room (with the first and the
        /// last such opportunity's clock), and how it ended. Off by default: a run asks for it (<c>-zantetsuDispatchRecord</c>, or
        /// the pool replay), and <c>-zantetsuDispatchObserveOff</c> keeps it off whatever asked. Nothing of it decides anything.
        /// </summary>
        public static volatile bool WorkLog;

        /// <summary>How many records one dispatcher keeps at most; past it a work is counted as not kept (never silently dropped).</summary>
        public const int WorkLogRoom = 262144;
    }

    /// <summary>How a logged work ended, or where it stood when the log was read (GRAPH-CONNECT-17 §3-E4).</summary>
    public enum DispatchWorkEnd
    {
        /// <summary>Still waiting in the dispatcher's queue (t0 only).</summary>
        Waiting = 0,
        /// <summary>Accepted by its destination, not yet begun by a worker (t0, t1).</summary>
        Accepted,
        /// <summary>A worker is inside it (t0, t1, t2).</summary>
        Running,
        /// <summary>Its worker finished it and it waits to be collected (t0 to t3).</summary>
        FinishedNotCollected,
        /// <summary>Collected, finished (t0 to t4).</summary>
        Collected,
        /// <summary>Collected, its Begin threw (t0 to t4).</summary>
        Failed,
        /// <summary>Accepted but never begun: a stop turned it into a cancellation the main thread collected (t0, t1, t4).</summary>
        NotRun,
        /// <summary>Never submitted: a stop cancelled it in the dispatcher's queue (t0 and the end's clock).</summary>
        CancelledWaiting,
        /// <summary>Never submitted: its owner took it back (<see cref="SharedWorkDispatcher.Cancel"/>; t0 and the end's clock).</summary>
        Withdrawn,
        /// <summary>Never submitted to Background: reclassified to another destination while waiting (t0 and the move's clock; not followed further).</summary>
        MovedOut,
    }

    /// <summary>
    /// One logged work (GRAPH-CONNECT-17 §3-E4). Clocks are <c>Stopwatch.GetTimestamp</c> ticks, 0 where not taken (never a 0
    /// interval). t1 is the pool's acceptance, stamped inside its lock before its queue has the work (the same stamp an observed
    /// work's NoteAccepted is told), so no worker can begin it earlier: t1 ≤ t2 holds for every work.
    /// </summary>
    public struct DispatchWorkRecord
    {
        public long serial;
        public WorkPurpose purpose;
        public WorkDestination destination;
        public string kind;
        /// <summary>The work's own number (<see cref="IDispatchLabeled"/>), or -1.</summary>
        public long label;
        public long t0, t1, t2, t3, t4, tEnd;
        /// <summary>Dispatch opportunities that could not submit it for want of its destination's room, and their first and last clocks.</summary>
        public int refused;
        public long firstRefusal, lastRefusal;
        /// <summary>The pool worker that ran it, or -1.</summary>
        public int worker;
        public DispatchWorkEnd end;
        /// <summary>A MovedOut work: the destination it was reclassified to (its destination and purpose above stay Background's).</summary>
        public bool moved;
        public WorkDestination movedTo;
    }
}
