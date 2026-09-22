using System;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Where a piece of work runs (DESIGN 4.3). Three places, each with its own room: work in one of them is never
    /// counted against another's.
    /// </summary>
    public enum WorkDestination
    {
        /// <summary>
        /// The Unity Job System, for urgent work: the physics safety of the current state and the physics of a cut
        /// that has been admitted. Unity's own worker count and priorities are left alone.
        /// </summary>
        UnityJob = 0,

        /// <summary>The always-on Geometry pool, for the numeric geometry work of an admitted cut.</summary>
        GeometryPool = 1,

        /// <summary>The always-on Background pool, for speculative and deferrable numeric work.</summary>
        BackgroundPool = 2,
    }

    /// <summary>
    /// What a piece of work is for. The destination follows from the purpose **at the moment it is submitted**
    /// (DESIGN 4.3, 4.4): work that has not been submitted yet, and work that becomes ready later, is classified by
    /// the purpose it has then, which is why a hit can change where a not-yet-submitted work will go.
    /// </summary>
    public enum WorkPurpose
    {
        /// <summary>The physics safety of the current state. Urgent.</summary>
        CurrentStatePhysicsSafety = 0,

        /// <summary>Physics for a cut that has already been admitted from a real hit. Urgent.</summary>
        AdmittedPhysics = 1,

        /// <summary>The shared geometry work of a cut that has already been admitted.</summary>
        AdmittedGeometry = 2,

        /// <summary>Speculative work for a hit that has not happened.</summary>
        Speculative = 3,

        /// <summary>Maintenance and optional quality.</summary>
        Maintenance = 4,
    }

    /// <summary>Which destination a purpose names, and which purposes are urgent (DESIGN 4.3).</summary>
    public static class WorkPurposes
    {
        /// <summary>Whether this is one of the defined purposes.</summary>
        public static bool IsDefined(WorkPurpose purpose)
        {
            return purpose >= WorkPurpose.CurrentStatePhysicsSafety && purpose <= WorkPurpose.Maintenance;
        }

        /// <summary>
        /// The destination this purpose is submitted to: physics to the Unity Job System, an admitted cut's geometry
        /// to the Geometry pool, and speculation and maintenance to the Background pool.
        /// </summary>
        public static WorkDestination DestinationOf(WorkPurpose purpose)
        {
            switch (purpose)
            {
                case WorkPurpose.CurrentStatePhysicsSafety:
                case WorkPurpose.AdmittedPhysics:
                    return WorkDestination.UnityJob;
                case WorkPurpose.AdmittedGeometry:
                    return WorkDestination.GeometryPool;
                case WorkPurpose.Speculative:
                case WorkPurpose.Maintenance:
                    return WorkDestination.BackgroundPool;
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a defined work purpose");
            }
        }

        /// <summary>Whether work of this purpose is urgent, which is to say whether it goes to the Unity Job System.</summary>
        public static bool IsUrgent(WorkPurpose purpose)
        {
            return DestinationOf(purpose) == WorkDestination.UnityJob;
        }
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

    /// <summary>How a piece of work ended.</summary>
    public enum WorkOutcome
    {
        /// <summary>It ran to the end. Whether its product is any good is the caller's judgement, not this.</summary>
        Finished = 0,

        /// <summary>It threw. The exception comes with it, and nothing re-runs it or sends it somewhere else.</summary>
        Failed = 1,

        /// <summary>It never started, and never will: a normal stop reached it while it was still waiting.</summary>
        Cancelled = 2,
    }

    /// <summary>
    /// How a piece of work ended, handed to <see cref="IDispatchWork.Collect"/> on the main thread. A failure on a
    /// pool worker travels to the main thread in here rather than being lost on the worker.
    /// </summary>
    public readonly struct WorkCompletion
    {
        private WorkCompletion(WorkOutcome outcome, Exception failure)
        {
            this.outcome = outcome;
            this.failure = failure;
        }

        public static WorkCompletion Finished => new WorkCompletion(WorkOutcome.Finished, null);

        public static WorkCompletion Cancelled => new WorkCompletion(WorkOutcome.Cancelled, null);

        public static WorkCompletion Failed(Exception failure)
        {
            if (failure == null)
            {
                throw new ArgumentNullException(nameof(failure), "a failure carries its exception");
            }

            return new WorkCompletion(WorkOutcome.Failed, failure);
        }

        public readonly WorkOutcome outcome;

        /// <summary>The exception, for <see cref="WorkOutcome.Failed"/> only; null otherwise.</summary>
        public readonly Exception failure;

        public bool Succeeded => outcome == WorkOutcome.Finished;

        public override string ToString()
        {
            return outcome == WorkOutcome.Failed ? "failed: " + failure.Message : outcome.ToString();
        }
    }

    /// <summary>
    /// One piece of work the shared dispatcher can begin and later collect. The dispatcher decides **when** and
    /// **where**, and nothing else: it never inspects a product, never decides whether one is adopted, and never
    /// publishes anything.
    /// <para>
    /// **The work owns what it needs.** A handle, its native containers, the input it reads and the scratch and
    /// output it writes — those belong to this object, from before <see cref="Begin"/> until the caller is done with
    /// it after <see cref="Collect"/>. The dispatcher frees nothing of it and keeps no reference afterwards. Work
    /// that runs on a pool must read only input nothing else writes while it runs, and must write only scratch and
    /// output no other work shares.
    /// </para>
    /// <para>
    /// **Which thread each call arrives on** belongs to the destination the purpose named. For
    /// <see cref="WorkDestination.UnityJob"/>, <see cref="Begin"/> runs on the main thread and is where a job is
    /// scheduled. For the two pools it runs on a pool worker and **is** the numeric body — the Burst direct call
    /// route, with whatever first call has to be prepared already prepared on the main thread — and it must not
    /// touch a Unity scene object: no GameObject, Component, Transform, Renderer or Rigidbody. Collection, adoption
    /// and publication stay on the main thread in every case.
    /// </para>
    /// <para>
    /// **If <see cref="Begin"/> throws** the two destinations differ, because a pool worker cannot let an exception
    /// escape. A pool catches it and brings it to the main thread as <see cref="WorkOutcome.Failed"/>; the work is
    /// collected once, with the exception, and is neither re-run nor sent to another destination. On the Unity Job
    /// destination the exception reaches whoever called <see cref="SharedWorkDispatcher.Dispatch"/>, because the
    /// dispatcher cannot tell whether a job was submitted first: the work stays counted as begun, is never begun
    /// again, is asked <see cref="IsComplete"/> at later opportunities and is collected once when that answers yes —
    /// which is how a job that really was submitted gets its resources back.
    /// </para>
    /// </summary>
    public interface IDispatchWork
    {
        /// <summary>
        /// Begins the work. Called once, on the thread that belongs to its destination, and never for work that was
        /// cancelled while it waited. Work begun here is never interrupted afterwards.
        /// </summary>
        void Begin();

        /// <summary>
        /// Whether the work has finished, asked without blocking. This is for the
        /// <see cref="WorkDestination.UnityJob"/> destination, which has no other way to know — for a job it is
        /// <c>JobHandle.IsCompleted</c>, and it must not wait, spin or force completion. A pool knows when its own
        /// worker returned and never asks this, so pool work may simply answer true.
        /// </summary>
        bool IsComplete { get; }

        /// <summary>
        /// Takes the finished work back, once, on the main thread. This is the caller's own moment to check
        /// generation, premises and authority (DESIGN 8) and to decide whether the product is adopted or reclaimed —
        /// the dispatcher takes no part in that, and calls this exactly once whatever the outcome was. Releasing the
        /// work's own resources belongs here too; if this throws, the dispatcher has already let the work go and will
        /// neither collect nor re-run it.
        /// </summary>
        void Collect(WorkCompletion completion);
    }

    /// <summary>
    /// One of the three destinations of DESIGN 4.3, as the dispatcher uses it. Every member is for the main thread
    /// only: the main thread is the only one that submits and the only one that takes finished work back.
    /// <para>
    /// **Its capacity is its own.** <see cref="Capacity"/> bounds everything this destination holds at once —
    /// waiting in its queue, running, and finished but not yet taken back — and it is a **bound**, not the initial
    /// size of a queue that could grow past it. Nothing here is counted against another destination's capacity, so
    /// external pool work never occupies the Unity Job System's room. When the main thread stops collecting, a
    /// destination fills up and then refuses work: that refusal is the bound doing its job.
    /// </para>
    /// </summary>
    public interface IWorkExecutor
    {
        /// <summary>Which of the three destinations this is.</summary>
        WorkDestination Destination { get; }

        /// <summary>The bound on what this destination holds at once: queued, running and finished-not-taken.</summary>
        int Capacity { get; }

        /// <summary>How many it holds now, by the same count as <see cref="Capacity"/>.</summary>
        int Held { get; }

        /// <summary>
        /// Whether it looks as though it would take one more piece of work: false when it is full, closed, or in no
        /// state to run anything. A cheap question asked before the work is taken out of the queue;
        /// <see cref="TryAccept"/> is the authority, and may still refuse.
        /// </summary>
        bool CanAccept { get; }

        /// <summary>
        /// Takes one piece of work into this destination's keeping, or refuses it. **Acceptance and refusal are what
        /// this decides, and nothing else runs here**: no work is begun, so nothing the work itself does can throw
        /// out of this.
        /// <para>
        /// False means it was **not** accepted — full, closed, or no longer able to run anything — and that nothing
        /// happened at all: the work is untouched and still the caller's, to offer again later. True means this
        /// destination now holds it and will hand it back through <see cref="TryTakeFinished"/> exactly once. A
        /// destination that decides this against state a worker can also change decides it at one synchronisation
        /// boundary, so that accepting and falling over cannot interleave.
        /// </para>
        /// </summary>
        bool TryAccept(IDispatchWork work);

        /// <summary>
        /// Begins work this destination has **already accepted**, where beginning it belongs to the main thread. The
        /// Unity Job destination schedules the job here; a pool has nothing to do, because its own worker begins the
        /// work it accepted. An exception out of the work's own <see cref="IDispatchWork.Begin"/> is not caught: the
        /// work has been accepted, so it stays held and is collected once, and the exception reaches the caller.
        /// </summary>
        void BeginAccepted(IDispatchWork work);

        /// <summary>
        /// Takes back one piece of work that has ended, without blocking, in no promised order. False when none has
        /// ended. What it holds drops by one, freeing room for new work.
        /// </summary>
        bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion);

        /// <summary>Stops accepting new work. What it already holds keeps running and is still collected.</summary>
        void CloseForNewWork();

        /// <summary>
        /// The normal stop (DESIGN 4.4), never the ordinary frame path. Closes for new work, turns whatever has not
        /// begun into <see cref="WorkOutcome.Cancelled"/> completions for the main thread to take once, waits for
        /// work that is already running to finish using what it holds, and stops the workers. Returns whether that
        /// was confirmed within <paramref name="timeoutMilliseconds"/>; what it holds is only released after it was.
        /// </summary>
        bool StopAndConfirm(int timeoutMilliseconds);
    }

    /// <summary>What one dispatch opportunity actually did.</summary>
    public readonly struct DispatchProgress
    {
        internal DispatchProgress(int collected, int submitted)
        {
            this.collected = collected;
            this.submitted = submitted;
        }

        /// <summary>How many ended works were collected.</summary>
        public readonly int collected;

        /// <summary>How many waiting works were submitted to a destination.</summary>
        public readonly int submitted;

        /// <summary>Whether this opportunity moved anything at all. A dispatch that did not is simply over.</summary>
        public bool MadeProgress => collected > 0 || submitted > 0;
    }

    /// <summary>What a normal stop did.</summary>
    public readonly struct DispatchShutdownResult
    {
        internal DispatchShutdownResult(int cancelled, int collected, bool workersStopped)
        {
            this.cancelled = cancelled;
            this.collected = collected;
            this.workersStopped = workersStopped;
        }

        /// <summary>How many works had not begun and were cancelled, each collected once.</summary>
        public readonly int cancelled;

        /// <summary>How many works had begun and were collected, each once.</summary>
        public readonly int collected;

        /// <summary>
        /// Whether every destination confirmed that running work had finished with what it holds and that its
        /// workers had stopped. False means the stop timed out, and resources were not declared free.
        /// </summary>
        public readonly bool workersStopped;
    }

    /// <summary>
    /// The main thread's shared dispatch of ready work (DESIGN 4.4): it takes work whose dependencies the caller has
    /// already resolved, submits it to the destination its purpose names (DESIGN 4.3) within bounded room and a
    /// per-frame budget, and collects what has ended without blocking.
    /// <para>
    /// **It decides when and where, not what.** Dependency resolution, adoption of a product and publication of
    /// anything at all stay with the caller: the dispatcher never touches a ledger, a geometry or a child. Its queue
    /// type is its own business and no subsystem's contract, and the state a work is in here is deliberately not the
    /// same enum as an operation's state in <see cref="LogicalCutLedger"/>. There is one dispatch: no second
    /// scheduler, no second DAG, and no layer that keeps an older arrangement alive.
    /// </para>
    /// <para>
    /// **Three destinations, three separate rooms.** A purpose names a destination and each destination bounds its
    /// own load, so external pool work is never counted against the Unity Job System's concurrency, and one
    /// destination being full never stops another. Urgent work is handled first where there is a choice — collected
    /// first, and offered first when several works could be submitted in one opportunity — but a destination that
    /// cannot take work right now is passed over rather than waited for, so urgent work that is ready or running
    /// never stops an independent external work from being submitted.
    /// </para>
    /// <para>
    /// **Handing work over is two separate things.** A destination first accepts the work or refuses it, and only
    /// then is it begun. Work a destination **refused** was never taken from anyone: it is still waiting here,
    /// unbegun, uncounted and offered again at the next opportunity. **Acceptance is where ownership passes**: from
    /// that moment the work is that destination's, which will hand it back exactly once, and the submitted record
    /// here is settled before the main thread can collect anything of it.
    /// </para>
    /// <para>
    /// The two destinations differ in when the work actually starts, and the record does not pretend otherwise. A
    /// pool's worker may begin accepted work **immediately** — before the submitted record is written and before
    /// <see cref="IWorkExecutor.BeginAccepted"/> is even called — and nothing here delays it to tidy that up: what
    /// matters is that ownership passed at acceptance and that the record is settled before collection. On the Unity
    /// Job destination beginning is the main thread's own next step, so there the record really is settled first, and
    /// a begin that throws still leaves the work submitted. Either way nothing is ever both refused and counted, or
    /// accepted and forgotten.
    /// </para>
    /// <para>
    /// **No total order.** Beyond taking urgent work first, nothing here promises an order: not between the two
    /// pools, not within one destination, not by deadline, and not by the order work was handed in. Low-priority
    /// work is allowed not to progress, and nothing promises a completion deadline.
    /// </para>
    /// <para>
    /// **Room kept for urgent work.** A positive part of the waiting capacity is for urgent purposes only, so work
    /// below them can never fill the queue and leave physics no way in. When there is no place a purpose may take,
    /// the work is simply not taken: it stays the caller's, to offer again at a later opportunity. Nothing is
    /// evicted, nothing is rescued synchronously, and nothing waits on the spot for room.
    /// </para>
    /// <para>
    /// **The frame budget.** <see cref="BeginFrame"/> refills the budget once per frame — calling it again with the
    /// same frame refills nothing. Each dispatch opportunity in that frame shares what is left. One collection costs
    /// one unit and one submission costs one unit. This counts **occasions, not time**: no real-time measurement
    /// takes part, and it is not a bound on main-thread duration — how long an unfinished check, a scan of the queue
    /// or any of the callbacks takes is no part of it. A collection is never held back because some work could not be
    /// submitted afterwards: collecting comes first, always.
    /// </para>
    /// <para>
    /// **Nothing waits, and nothing re-enters.** Work that has not ended is passed over, not waited for: no busy
    /// polling, no spin, no forced completion, no re-entering the player loop, and no waiting for everything to
    /// finish. An opportunity that collected nothing and submitted nothing is over. A callback may offer more work
    /// with <see cref="TryEnqueue"/> — that is ordinary — but calling <see cref="Dispatch"/>, <see cref="BeginFrame"/>
    /// or <see cref="Shutdown"/> from inside a dispatch is refused before anything changes.
    /// </para>
    /// <para>
    /// **After a hit.** Work the caller has offered but that has not been submitted yet can be classified again with
    /// <see cref="TryReclassify"/> — that is what a hit does to a speculation that never left the waiting queue, and
    /// to the successors that become ready after it. Work that has already been submitted is not: it continues at
    /// the destination it was given to, queue wait included, and is never moved, promoted or issued a second time.
    /// <see cref="IsWaiting"/> and <see cref="IsSubmitted"/> are how those two states are told apart.
    /// </para>
    /// <para>
    /// **Ownership.** Before it is taken, the work is the caller's. While it waits, the dispatcher holds it, and
    /// <see cref="Cancel"/> gives it straight back. Once begun, everything it needs stays owned by the work object
    /// itself (see <see cref="IDispatchWork"/>); the dispatcher holds only the right to collect it exactly once, with
    /// whatever outcome it ended in. It never interrupts work and never frees anything belonging to a work. After
    /// Collect the work is the caller's again, adopted or reclaimed by the caller's own judgement.
    /// </para>
    /// <para>
    /// **When a callback throws**, the dispatcher does not catch it — the exception reaches whoever called
    /// <see cref="Dispatch"/> — and what it promises is what it will not do afterwards. A <c>Collect</c> that threw
    /// has already been taken out and paid for, and is neither collected again nor re-run; from that moment the work,
    /// and whatever it still holds, is the caller's.
    /// </para>
    /// </summary>
    public sealed class SharedWorkDispatcher
    {
        private struct Entry
        {
            public WorkTicket ticket;
            public WorkPurpose purpose;
            public WorkDestination destination;
            public int sequence;
            public IDispatchWork work;
        }

        // The order destinations are offered in within one opportunity: urgent first where there is a choice.
        private static readonly WorkDestination[] Order =
        {
            WorkDestination.UnityJob,
            WorkDestination.GeometryPool,
            WorkDestination.BackgroundPool,
        };

        private readonly List<Entry> _waiting = new List<Entry>();
        private readonly List<Entry> _submitted = new List<Entry>();
        private readonly IWorkExecutor[] _executors = new IWorkExecutor[3];

        private int _lastTicket;
        private int _lastSequence;
        private int _frameId;
        private bool _frameOpen;
        private bool _dispatching;

        /// <param name="waitingCapacity">How many works may wait here at once, over every purpose.</param>
        /// <param name="reservedForUrgent">
        /// How many of those places only urgent purposes may take. Positive, and smaller than the capacity: there
        /// must always be a way in for physics, and always somewhere for other work to go (DESIGN 4.4).
        /// </param>
        /// <param name="frameBudget">
        /// How many collections and submissions together one frame allows. Refilled by <see cref="BeginFrame"/>,
        /// shared by every dispatch opportunity in that frame. A count of occasions, not a time limit.
        /// </param>
        /// <param name="unityJob">The urgent destination: the Unity Job System.</param>
        /// <param name="geometryPool">The Geometry pool destination.</param>
        /// <param name="backgroundPool">The Background pool destination.</param>
        public SharedWorkDispatcher(
            int waitingCapacity,
            int reservedForUrgent,
            int frameBudget,
            IWorkExecutor unityJob,
            IWorkExecutor geometryPool,
            IWorkExecutor backgroundPool)
        {
            if (waitingCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(waitingCapacity), "must be positive");
            }

            if (reservedForUrgent <= 0 || reservedForUrgent >= waitingCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reservedForUrgent), "must be positive and must leave room for other work");
            }

            if (frameBudget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameBudget), "must be positive");
            }

            Take(unityJob, WorkDestination.UnityJob, nameof(unityJob));
            Take(geometryPool, WorkDestination.GeometryPool, nameof(geometryPool));
            Take(backgroundPool, WorkDestination.BackgroundPool, nameof(backgroundPool));

            WaitingCapacity = waitingCapacity;
            ReservedForUrgent = reservedForUrgent;
            FrameBudget = frameBudget;
        }

        private void Take(IWorkExecutor executor, WorkDestination destination, string name)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(name);
            }

            if (executor.Destination != destination)
            {
                throw new ArgumentException("this executor is for " + executor.Destination + ", not " + destination, name);
            }

            if (executor.Capacity <= 0)
            {
                throw new ArgumentException("a destination must have room for at least one work", name);
            }

            for (int i = 0; i < _executors.Length; i++)
            {
                if (ReferenceEquals(_executors[i], executor))
                {
                    throw new ArgumentException("one executor cannot serve two destinations", name);
                }
            }

            _executors[(int)destination] = executor;
        }

        public int WaitingCapacity { get; }
        public int ReservedForUrgent { get; }
        public int FrameBudget { get; }

        /// <summary>What is left of this frame's budget. Zero until the first <see cref="BeginFrame"/>.</summary>
        public int RemainingBudget { get; private set; }

        /// <summary>How many works are waiting here, not yet submitted to any destination.</summary>
        public int WaitingCount => _waiting.Count;

        /// <summary>How many works have been submitted and not yet collected, over all three destinations.</summary>
        public int SubmittedCount => _submitted.Count;

        /// <summary>Whether a normal stop has closed this dispatcher for new work.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>How many works have been submitted, and collected, over the dispatcher's life.</summary>
        public int TotalSubmitted { get; private set; }
        public int TotalCollected { get; private set; }

        /// <summary>The destination, as the dispatcher holds it. For reading its capacity and load.</summary>
        public IWorkExecutor Executor(WorkDestination destination)
        {
            int index = (int)destination;
            if (index < 0 || index >= _executors.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(destination), destination, "not a destination");
            }

            return _executors[index];
        }

        /// <summary>How many works are waiting here for one destination.</summary>
        public int WaitingCountFor(WorkDestination destination)
        {
            int count = 0;
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (_waiting[i].destination == destination)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>How many submitted works this destination holds, as the dispatcher counts them.</summary>
        public int SubmittedCountFor(WorkDestination destination)
        {
            int count = 0;
            for (int i = 0; i < _submitted.Count; i++)
            {
                if (_submitted[i].destination == destination)
                {
                    count++;
                }
            }

            return count;
        }

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
        /// Offers a ready work for a purpose. Returns false, with the ticket unset, when the waiting queue has no
        /// place this purpose may take, or when a normal stop has closed the dispatcher — the work is then still
        /// entirely the caller's, unchanged, to offer again later. Nothing already waiting or running is evicted to
        /// make room. This may be called from inside <see cref="IDispatchWork.Collect"/>: work that became ready is
        /// offered in the ordinary way.
        /// </summary>
        public bool TryEnqueue(WorkPurpose purpose, IDispatchWork work, out WorkTicket ticket)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (!WorkPurposes.IsDefined(purpose))
            {
                throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a defined work purpose");
            }

            ticket = default;
            if (IsClosed || !HasPlaceFor(purpose))
            {
                return false;
            }

            ticket = new WorkTicket(checked(_lastTicket + 1));
            _lastTicket = ticket.value;
            _waiting.Add(new Entry
            {
                ticket = ticket,
                purpose = purpose,
                destination = WorkPurposes.DestinationOf(purpose),
                sequence = checked(++_lastSequence),
                work = work,
            });
            return true;
        }

        /// <summary>
        /// Takes a work that is still waiting back out of the queue, giving it to the caller unchanged and
        /// uncollected. Work that has already been submitted cannot be cancelled — it is never interrupted — and
        /// this returns false for it.
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

        /// <summary>
        /// Gives a work that is **still waiting** a new purpose, and with it the destination that purpose names.
        /// This is how a hit reclassifies what has not been submitted yet (DESIGN 4.4): a speculation that never left
        /// the queue becomes urgent physics or admitted geometry, at the purpose it has now.
        /// <para>
        /// Returns false for a work that has already been submitted, which continues where it is and is never moved,
        /// promoted or issued again, and for a ticket this dispatcher does not hold. Reclassifying does not need room
        /// at the new destination and reserves nothing there: the room is decided when it is submitted.
        /// </para>
        /// <para>
        /// The one thing it will not do is undermine the room kept for urgent work: giving an urgent work a
        /// non-urgent purpose is refused when that would leave more non-urgent works waiting than the unreserved
        /// part of the queue allows. The work then keeps the purpose it had, exactly as when the queue is full, and
        /// the reservation holds without exception.
        /// </para>
        /// </summary>
        public bool TryReclassify(WorkTicket ticket, WorkPurpose purpose)
        {
            if (!WorkPurposes.IsDefined(purpose))
            {
                throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a defined work purpose");
            }

            for (int i = 0; i < _waiting.Count; i++)
            {
                if (_waiting[i].ticket != ticket)
                {
                    continue;
                }

                Entry entry = _waiting[i];

                // Leaving urgent for something below it must not park a non-urgent work in a place kept for urgent
                // work. This entry is urgent, so it is not one of the ones counted here.
                if (WorkPurposes.IsUrgent(entry.purpose)
                    && !WorkPurposes.IsUrgent(purpose)
                    && CountNonUrgentWaiting() >= WaitingCapacity - ReservedForUrgent)
                {
                    return false;
                }

                entry.purpose = purpose;
                entry.destination = WorkPurposes.DestinationOf(purpose);
                _waiting[i] = entry;
                return true;
            }

            return false;
        }

        /// <summary>Whether this ticket names work that is waiting here and could still be cancelled or reclassified.</summary>
        public bool IsWaiting(WorkTicket ticket)
        {
            return IndexIn(_waiting, ticket) >= 0;
        }

        /// <summary>Whether this ticket names work that has been submitted to a destination and not yet collected.</summary>
        public bool IsSubmitted(WorkTicket ticket)
        {
            return IndexIn(_submitted, ticket) >= 0;
        }

        /// <summary>
        /// Where this work is going, or where it has gone. False for a ticket this dispatcher does not hold.
        /// </summary>
        public bool TryGetDestination(WorkTicket ticket, out WorkDestination destination)
        {
            int waiting = IndexIn(_waiting, ticket);
            if (waiting >= 0)
            {
                destination = _waiting[waiting].destination;
                return true;
            }

            int submitted = IndexIn(_submitted, ticket);
            if (submitted >= 0)
            {
                destination = _submitted[submitted].destination;
                return true;
            }

            destination = default;
            return false;
        }

        /// <summary>
        /// One dispatch opportunity: collect what has ended, then submit what the destinations and the remaining
        /// budget allow. Several of these may run in one frame, sharing that frame's budget. Returns what it moved;
        /// an opportunity that moved nothing is simply over, and the caller need not call again until it has reason
        /// to. Refused, before anything changes, if called from inside another dispatch.
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
                // Collection first, and on its own terms: whether anything could be submitted afterwards has no
                // bearing on taking an ended work back. Urgent first, since that is where a choice exists.
                //
                // **Nothing submitted, nothing to take back.** Every work a destination can hand back was accepted
                // here and recorded in _submitted in the same step, and stays recorded until it is collected, so an
                // empty record means no destination holds anything of ours. This is not "nothing came back last
                // time": while the record still holds work and the budget allows, each destination in turn is asked
                // again, because a worker may have finished in the meantime. **The budget's end stops this pass as it
                // always did; emptying the record is what is new here** -- the base had no such condition and went on
                // asking the destinations after it while budget remained. The destination whose collection empties the
                // record still finishes its own loop, asking once more and being told no; what is skipped is the
                // destinations after it. The stop's own collection below asks regardless of the record.
                int collected = 0;
                for (int d = 0; _submitted.Count > 0 && d < Order.Length && RemainingBudget > 0; d++)
                {
                    IWorkExecutor executor = _executors[(int)Order[d]];
                    while (RemainingBudget > 0
                        && executor.TryTakeFinished(out IDispatchWork work, out WorkCompletion completion))
                    {
                        // Taken out and paid for before the callback runs: a Collect that throws is not collected
                        // again and not re-run, and the work is the caller's from that moment.
                        Forget(work);
                        RemainingBudget--;
                        collected++;
                        TotalCollected++;
                        work.Collect(completion);
                    }
                }

                // Then submission, urgent first. A destination with no room right now is passed over, never waited
                // for, so work that cannot go in does not stop independent work bound elsewhere from going in.
                //
                // **Nothing waiting, nothing to offer.** The queue is read here, after the collection above, so work
                // a Collect callback offered is submitted in this same opportunity, as it always was.
                int submitted = 0;
                for (int d = 0; _waiting.Count > 0 && d < Order.Length && RemainingBudget > 0; d++)
                {
                    WorkDestination destination = Order[d];
                    IWorkExecutor executor = _executors[(int)destination];
                    while (RemainingBudget > 0 && executor.CanAccept && TryTakeNextWaiting(destination, out Entry next))
                    {
                        // Acceptance first, and nothing is charged or moved until it is decided. A destination that
                        // refuses — it filled up, or fell over, between the question and the answer — has taken
                        // nothing: the work goes straight back to waiting here, unbegun and uncounted, for a later
                        // opportunity. Nothing about it is lost between the two of us.
                        if (!executor.TryAccept(next.work))
                        {
                            _waiting.Add(next);
                            break;
                        }

                        // Accepted: ownership passed at that moment, and the submitted record is settled here,
                        // before anything of it can be collected. A pool's worker may already be running it — the
                        // record does not gate that and nothing delays it. On the Unity Job destination beginning
                        // is the next line, so there the record precedes it; if beginning throws we cannot know
                        // whether a job was submitted first, so the work stays here — never begun twice, still
                        // asked about, and collected once when it ends.
                        RemainingBudget--;
                        submitted++;
                        TotalSubmitted++;
                        _submitted.Add(next);
                        executor.BeginAccepted(next.work);
                    }
                }

                return new DispatchProgress(collected, submitted);
            }
            finally
            {
                _dispatching = false;
            }
        }

        /// <summary>
        /// The normal stop (DESIGN 4.4). Closes the dispatcher and every destination for new work, gives each work
        /// that never began exactly one terminal handling as <see cref="WorkOutcome.Cancelled"/>, waits for work that
        /// is already running to finish using what it holds and for the workers to stop, and only then collects what
        /// ended — each work once, whatever it ended as.
        /// <para>
        /// **A stop that was not confirmed keeps everything.** When a destination reports that it could not confirm
        /// within the timeout, <see cref="DispatchShutdownResult.workersStopped"/> is false and the work that has not
        /// ended is still held there, with everything it owns: nothing is collected early and nothing is forced to
        /// finish. Calling this again is how the stop is confirmed later; each work is collected once, when it
        /// really has ended, and nothing is collected or cancelled twice.
        /// </para>
        /// <para>
        /// This is not the ordinary frame path and no part of it: it is allowed to wait, it ignores the frame budget,
        /// and nothing in a normal frame calls it. It is refused from inside a dispatch.
        /// </para>
        /// </summary>
        /// <param name="timeoutMilliseconds">How long each destination may take to confirm that it has stopped.</param>
        public DispatchShutdownResult Shutdown(int timeoutMilliseconds)
        {
            if (_dispatching)
            {
                throw new InvalidOperationException("Shutdown cannot be called from inside a dispatch");
            }

            if (timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "must not be negative");
            }

            _dispatching = true;
            try
            {
                IsClosed = true;
                for (int d = 0; d < Order.Length; d++)
                {
                    _executors[(int)Order[d]].CloseForNewWork();
                }

                // Work that never left this queue never began: one terminal handling each, and then it is gone.
                int cancelled = 0;
                while (_waiting.Count > 0)
                {
                    Entry entry = _waiting[_waiting.Count - 1];
                    _waiting.RemoveAt(_waiting.Count - 1);
                    cancelled++;
                    entry.work.Collect(WorkCompletion.Cancelled);
                }

                // Only after a destination has confirmed that its running work is done with what it holds and its
                // workers have stopped is any of it declared free.
                bool stopped = true;
                for (int d = 0; d < Order.Length; d++)
                {
                    stopped &= _executors[(int)Order[d]].StopAndConfirm(timeoutMilliseconds);
                }

                int collected = 0;
                for (int d = 0; d < Order.Length; d++)
                {
                    IWorkExecutor executor = _executors[(int)Order[d]];
                    while (executor.TryTakeFinished(out IDispatchWork work, out WorkCompletion completion))
                    {
                        Forget(work);
                        collected++;
                        TotalCollected++;
                        work.Collect(completion);
                    }
                }

                return new DispatchShutdownResult(cancelled, collected, stopped);
            }
            finally
            {
                _dispatching = false;
            }
        }

        private static int IndexIn(List<Entry> entries, WorkTicket ticket)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ticket == ticket)
                {
                    return i;
                }
            }

            return -1;
        }

        private void Forget(IDispatchWork work)
        {
            for (int i = 0; i < _submitted.Count; i++)
            {
                if (ReferenceEquals(_submitted[i].work, work))
                {
                    _submitted.RemoveAt(i);
                    return;
                }
            }
        }

        // The next waiting work for one destination. Which one that is within a destination is this queue's own
        // business: no order is promised there, and none is relied on.
        private bool TryTakeNextWaiting(WorkDestination destination, out Entry entry)
        {
            int best = -1;
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (_waiting[i].destination != destination)
                {
                    continue;
                }

                if (best < 0 || _waiting[i].sequence < _waiting[best].sequence)
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

        // Urgent work may take any free place; everything else must leave the reserved ones alone.
        private bool HasPlaceFor(WorkPurpose purpose)
        {
            if (_waiting.Count >= WaitingCapacity)
            {
                return false;
            }

            if (WorkPurposes.IsUrgent(purpose))
            {
                return true;
            }

            return CountNonUrgentWaiting() < WaitingCapacity - ReservedForUrgent;
        }

        private int CountNonUrgentWaiting()
        {
            int count = 0;
            for (int i = 0; i < _waiting.Count; i++)
            {
                if (!WorkPurposes.IsUrgent(_waiting[i].purpose))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
