using System;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// What a piece of work means, in the order the shared dispatcher prefers it (DESIGN 4.4). The numeric order is
    /// the priority order, and work of the same priority keeps the order it was handed in.
    /// </summary>
    public enum WorkPriority
    {
        /// <summary>The physics safety of the current state: nothing is allowed to crowd this out.</summary>
        CurrentStatePhysicsSafety = 0,

        /// <summary>Physics for a cut that has already been admitted from a real hit.</summary>
        HitPhysics = 1,

        /// <summary>Finishing the geometry of a cut that has already been admitted from a real hit.</summary>
        HitGeometryFinish = 2,

        /// <summary>Speculative work for a hit that has not happened.</summary>
        Speculative = 3,

        /// <summary>Maintenance and optional quality: only when there is room left over.</summary>
        Maintenance = 4,
    }

    /// <summary>
    /// A handle for one piece of work that was taken into the dispatcher's waiting queue. Positive and never reused
    /// within a dispatcher; zero names nothing.
    /// </summary>
    public readonly struct WorkTicket : IEquatable<WorkTicket>
    {
        public const int Unset = 0;

        public readonly int value;

        public WorkTicket(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "a ticket is zero (unset) or positive");
            }

            this.value = value;
        }

        public bool IsSet => value > Unset;

        public bool Equals(WorkTicket other) => value == other.value;
        public override bool Equals(object obj) => obj is WorkTicket other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(WorkTicket a, WorkTicket b) => a.value == b.value;
        public static bool operator !=(WorkTicket a, WorkTicket b) => a.value != b.value;
        public override string ToString() => IsSet ? "work " + value : "work (unset)";
    }

    /// <summary>
    /// One piece of work the shared dispatcher can start and later collect, on the main thread. The dispatcher decides
    /// **when** these are called and nothing else: it never inspects a product, never decides whether one is adopted,
    /// and never publishes anything.
    /// <para>
    /// **The work owns what the job needs.** A handle, its native containers, anything the job reads or writes — those
    /// belong to this object, from before <see cref="Schedule"/> until the caller is done with it after
    /// <see cref="Collect"/>. The dispatcher frees nothing of it and holds no reference to any of it.
    /// </para>
    /// <para>
    /// **If <see cref="Schedule"/> throws**, the dispatcher cannot tell whether a job was submitted first, so it keeps
    /// the work as started: it will not be scheduled again, and it will be asked <see cref="IsComplete"/> at later
    /// opportunities and collected once when that answers yes. An implementation whose Schedule can throw must
    /// therefore still answer IsComplete safely afterwards, and must release in <see cref="Collect"/> whatever it did
    /// manage to take — that is how a job that really was submitted gets its resources back.
    /// </para>
    /// </summary>
    public interface IDispatchWork
    {
        /// <summary>
        /// Starts the work — for a job, this is where it is scheduled. Called once, on the main thread, and never for
        /// work that was cancelled while it waited. A job started here is never interrupted afterwards, and this is
        /// never called a second time for the same work, whether or not it threw.
        /// </summary>
        void Schedule();

        /// <summary>
        /// Whether the work has finished, asked without blocking. For a job this is <c>JobHandle.IsCompleted</c>; it
        /// must not wait, spin or force completion. It may be asked at any later opportunity, including after a
        /// <see cref="Schedule"/> that threw.
        /// </summary>
        bool IsComplete { get; }

        /// <summary>
        /// Takes the finished work back, once. This is the caller's own moment to check generation, premises and
        /// authority (DESIGN 8) and to decide whether the product is adopted or reclaimed — the dispatcher takes no
        /// part in that, and calls this exactly once either way. Releasing the work's own resources belongs here too;
        /// if this throws, the dispatcher has already let the work go and will neither collect nor re-run it.
        /// </summary>
        void Collect();
    }

    /// <summary>What one dispatch opportunity actually did.</summary>
    public readonly struct DispatchProgress
    {
        internal DispatchProgress(int collected, int scheduled)
        {
            this.collected = collected;
            this.scheduled = scheduled;
        }

        /// <summary>How many finished works were collected.</summary>
        public readonly int collected;

        /// <summary>How many waiting works were started.</summary>
        public readonly int scheduled;

        /// <summary>Whether this opportunity moved anything at all. A dispatch that did not is simply over.</summary>
        public bool MadeProgress => collected > 0 || scheduled > 0;
    }

    /// <summary>
    /// The main thread's shared dispatcher for ready work (DESIGN 4.4): it takes work whose dependencies the caller
    /// has already resolved, starts it in priority order within a bounded waiting queue, a bounded number of
    /// concurrent works and a per-frame budget, and collects what has finished without blocking.
    /// <para>
    /// **It decides when, not what.** Dependency resolution, adoption of a product and publication of anything at all
    /// stay with the caller: the dispatcher never touches a ledger, a geometry or a child. Its queue type is its own
    /// business and no subsystem's contract, and the state a work is in here is deliberately not the same enum as an
    /// operation's state in <see cref="LogicalCutLedger"/>.
    /// </para>
    /// <para>
    /// **Priority.** <see cref="WorkPriority"/> in order, and within one priority the order the work was handed in.
    /// Strict deadline order is not attempted and low-priority work is allowed not to progress; nothing here promises
    /// a completion deadline.
    /// </para>
    /// <para>
    /// **Room kept for physics.** A positive part of the waiting capacity is reserved for
    /// <see cref="WorkPriority.CurrentStatePhysicsSafety"/> and <see cref="WorkPriority.HitPhysics"/>, so work below
    /// them can never fill the queue and leave physics no way in. When the queue is full the work is simply not taken:
    /// it stays the caller's, to offer again at a later opportunity, and nothing is evicted to make room.
    /// </para>
    /// <para>
    /// **The frame budget.** <see cref="BeginFrame"/> refills the budget once per frame — calling it again with the
    /// same frame refills nothing. Each dispatch opportunity in that frame shares what is left. One collection costs
    /// one unit and one schedule costs one unit. This counts **occasions, not time**: no real-time measurement takes
    /// part, and it is not a bound on main-thread duration — how long an unfinished check, a scan of the queue or any
    /// of the callbacks takes is no part of it. A collection is never held back because a low-priority work could not
    /// be started: collecting comes first, always.
    /// </para>
    /// <para>
    /// **Nothing waits, and nothing re-enters.** An unfinished work is passed over, not waited for: no busy polling,
    /// no spin, no forced completion, no re-entering the player loop, and no waiting for everything to finish. An
    /// opportunity that collected nothing and started nothing is over. A callback may offer more work with
    /// <see cref="TryEnqueue"/> — that is ordinary — but calling <see cref="Dispatch"/> or <see cref="BeginFrame"/>
    /// from inside a dispatch is refused before anything changes, so no inner pass can schedule or collect while an
    /// outer one is in the middle of doing so, and no budget is refilled underneath it.
    /// </para>
    /// <para>
    /// **Ownership.** Before it is taken, the work is the caller's. While it waits, the dispatcher holds it, and
    /// <see cref="Cancel"/> gives it back. Once started, everything the job needs stays owned by the work object
    /// itself (see <see cref="IDispatchWork"/>); the dispatcher holds only the right to ask
    /// <see cref="IDispatchWork.IsComplete"/> and to call <see cref="IDispatchWork.Collect"/> exactly once. It never
    /// interrupts a job and never frees anything belonging to a work. After Collect the work is the caller's again,
    /// adopted or reclaimed by the caller's own judgement.
    /// </para>
    /// <para>
    /// **When a callback throws**, the dispatcher does not catch it — the exception reaches whoever called
    /// <see cref="Dispatch"/> — and what it promises is what it will not do afterwards. A <c>Schedule</c> that threw
    /// leaves the work counted as started and still held here, never scheduled again, still asked about and collected
    /// once when it reports finished. A <c>Collect</c> that threw has already been taken out and paid for, and is
    /// neither collected again nor re-run; from that moment the work, and whatever it still holds, is the caller's.
    /// </para>
    /// </summary>
    public sealed class SharedWorkDispatcher
    {
        private struct Entry
        {
            public WorkTicket ticket;
            public WorkPriority priority;
            public int sequence;
            public IDispatchWork work;
        }

        private readonly List<Entry> _waiting = new List<Entry>();
        private readonly List<Entry> _scheduled = new List<Entry>();

        private int _lastTicket;
        private int _lastSequence;
        private int _frameId;
        private bool _frameOpen;
        private bool _dispatching;

        /// <param name="waitingCapacity">How many works may wait at once, over every priority.</param>
        /// <param name="reservedForPhysics">
        /// How many of those places only the two physics priorities may take. Positive, and smaller than the capacity:
        /// there must always be a way in for physics, and always somewhere for other work to go (DESIGN 4.4).
        /// </param>
        /// <param name="maxConcurrent">How many works may be started and not yet collected at once.</param>
        /// <param name="framebudget">
        /// How many collections and schedules together one frame allows. Refilled by <see cref="BeginFrame"/>, shared
        /// by every dispatch opportunity in that frame. A count of occasions, not a time limit.
        /// </param>
        public SharedWorkDispatcher(int waitingCapacity, int reservedForPhysics, int maxConcurrent, int framebudget)
        {
            if (waitingCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(waitingCapacity), "must be positive");
            }

            if (reservedForPhysics <= 0 || reservedForPhysics >= waitingCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reservedForPhysics), "must be positive and must leave room for other work");
            }

            if (maxConcurrent <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent), "must be positive");
            }

            if (framebudget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(framebudget), "must be positive");
            }

            WaitingCapacity = waitingCapacity;
            ReservedForPhysics = reservedForPhysics;
            MaxConcurrent = maxConcurrent;
            FrameBudget = framebudget;
        }

        public int WaitingCapacity { get; }
        public int ReservedForPhysics { get; }
        public int MaxConcurrent { get; }
        public int FrameBudget { get; }

        /// <summary>What is left of this frame's budget. Zero until the first <see cref="BeginFrame"/>.</summary>
        public int RemainingBudget { get; private set; }

        public int WaitingCount => _waiting.Count;
        public int ScheduledCount => _scheduled.Count;

        /// <summary>How many works have been started, and collected, over the dispatcher's life.</summary>
        public int TotalScheduled { get; private set; }
        public int TotalCollected { get; private set; }

        /// <summary>
        /// Opens a frame, refilling the budget. Calling it again with the same frame does nothing at all, so the
        /// several dispatch opportunities of one frame share one budget. Refused from inside a dispatch, before
        /// anything changes: a callback must not move the frame under the pass that is running.
        /// </summary>
        public void BeginFrame(int frameId)
        {
            if (_dispatching)
            {
                throw new InvalidOperationException("a frame cannot be opened from inside a dispatch");
            }

            if (_frameOpen && _frameId == frameId)
            {
                return;
            }

            _frameId = frameId;
            _frameOpen = true;
            RemainingBudget = FrameBudget;
        }

        /// <summary>
        /// Offers a ready work. Returns false, with the ticket unset, when the waiting queue has no place this
        /// priority may take — the work is then still entirely the caller's, unchanged, to offer again later. Nothing
        /// already waiting or running is evicted to make room. This may be called from inside
        /// <see cref="IDispatchWork.Collect"/>: work that became ready is offered in the ordinary way.
        /// </summary>
        public bool TryEnqueue(WorkPriority priority, IDispatchWork work, out WorkTicket ticket)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (priority < WorkPriority.CurrentStatePhysicsSafety || priority > WorkPriority.Maintenance)
            {
                throw new ArgumentOutOfRangeException(nameof(priority), "not a defined work priority");
            }

            ticket = default;
            if (!HasPlaceFor(priority))
            {
                return false;
            }

            ticket = new WorkTicket(checked(_lastTicket + 1));
            _lastTicket = ticket.value;
            _waiting.Add(new Entry
            {
                ticket = ticket,
                priority = priority,
                sequence = checked(++_lastSequence),
                work = work,
            });
            return true;
        }

        /// <summary>
        /// Takes a work that is still waiting back out of the queue, giving it to the caller. Work that has already
        /// been started cannot be cancelled — a job is never interrupted — and this returns false for it.
        /// </summary>
        public bool Cancel(WorkTicket ticket)
        {
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (_waiting[i].ticket == ticket)
                {
                    _waiting.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether this ticket names work that is waiting and could still be cancelled.</summary>
        public bool IsWaiting(WorkTicket ticket)
        {
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (_waiting[i].ticket == ticket)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether this ticket names work that has been started and not yet collected.</summary>
        public bool IsScheduled(WorkTicket ticket)
        {
            for (int i = 0; i < _scheduled.Count; i++)
            {
                if (_scheduled[i].ticket == ticket)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// One dispatch opportunity: collect what has finished, then start what the priority order, the concurrency
        /// limit and the remaining budget allow. Several of these may run in one frame, sharing that frame's budget.
        /// Returns what it moved; an opportunity that moved nothing is simply over, and the caller need not call again
        /// until it has reason to. Refused, before anything changes, if called from inside another dispatch.
        /// </summary>
        public DispatchProgress Dispatch()
        {
            if (!_frameOpen)
            {
                throw new InvalidOperationException("BeginFrame must open a frame before work is dispatched");
            }

            if (_dispatching)
            {
                throw new InvalidOperationException("Dispatch cannot be called from inside a dispatch");
            }

            _dispatching = true;
            try
            {
                // Collection first, and on its own terms: whether a low-priority work could be started afterwards has
                // no bearing on taking a finished one back.
                int collected = 0;
                for (int i = 0; i < _scheduled.Count && RemainingBudget > 0;)
                {
                    Entry entry = _scheduled[i];
                    if (!entry.work.IsComplete)
                    {
                        // Not finished: passed over, never waited for.
                        i++;
                        continue;
                    }

                    // Taken out and paid for before the callback runs: a Collect that throws is not collected again
                    // and not re-run, and the work is the caller's from that moment.
                    _scheduled.RemoveAt(i);
                    RemainingBudget--;
                    collected++;
                    TotalCollected++;
                    entry.work.Collect();
                }

                int scheduled = 0;
                while (RemainingBudget > 0 && _scheduled.Count < MaxConcurrent && TryTakeNext(out Entry next))
                {
                    // Counted and held as started before the callback runs. If Schedule throws we cannot know whether
                    // a job was submitted first, so the work stays here: never scheduled twice, still asked about, and
                    // collected once when it reports finished — which is how a submitted job's resources come back.
                    RemainingBudget--;
                    scheduled++;
                    TotalScheduled++;
                    _scheduled.Add(next);
                    next.work.Schedule();
                }

                return new DispatchProgress(collected, scheduled);
            }
            finally
            {
                _dispatching = false;
            }
        }

        // The priority order, stable within a priority by the order the work was handed in.
        private bool TryTakeNext(out Entry entry)
        {
            int best = -1;
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (best < 0
                    || _waiting[i].priority < _waiting[best].priority
                    || (_waiting[i].priority == _waiting[best].priority && _waiting[i].sequence < _waiting[best].sequence))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                entry = default;
                return false;
            }

            entry = _waiting[best];
            _waiting.RemoveAt(best);
            return true;
        }

        // Physics may take any free place; everything else must leave the reserved ones alone.
        private bool HasPlaceFor(WorkPriority priority)
        {
            if (_waiting.Count >= WaitingCapacity)
            {
                return false;
            }

            if (priority == WorkPriority.CurrentStatePhysicsSafety || priority == WorkPriority.HitPhysics)
            {
                return true;
            }

            return CountNonPhysicsWaiting() < WaitingCapacity - ReservedForPhysics;
        }

        private int CountNonPhysicsWaiting()
        {
            int count = 0;
            for (int i = 0; i < _waiting.Count; i++)
            {
                WorkPriority priority = _waiting[i].priority;
                if (priority != WorkPriority.CurrentStatePhysicsSafety && priority != WorkPriority.HitPhysics)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
