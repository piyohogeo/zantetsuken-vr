using System;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Something with main-thread work of its own to carry forward between a collection and the next submission: it
    /// takes what the dispatcher has already handed back, does what only the main thread may do with it, and offers
    /// whatever that made ready.
    /// <para>
    /// <see cref="Pump"/> returns whether anything really moved — a stage reached, a resource taken or given back, a
    /// piece of work ended. Staying where it was is not movement: a reservation refused, a queue with no place, a
    /// dependency not yet met. A frame goes round again on movement, so anything that reported movement for standing
    /// still would keep it going for nothing.
    /// </para>
    /// <para>
    /// It waits for nothing. Work a worker has not finished is left alone, and nothing here is completed by force.
    /// </para>
    /// </summary>
    public interface IMainThreadPump
    {
        /// <summary>Carries what can be carried forward now, and says whether anything moved.</summary>
        bool Pump();
    }

    /// <summary>What one frame's update came to.</summary>
    public readonly struct SharedWorkFrameProgress
    {
        internal SharedWorkFrameProgress(int occasions, int collected, int submitted, int mainThreadSteps)
        {
            this.occasions = occasions;
            this.collected = collected;
            this.submitted = submitted;
            this.mainThreadSteps = mainThreadSteps;
        }

        /// <summary>How many times round the collect-carry-submit cycle this update went.</summary>
        public readonly int occasions;

        /// <summary>How many finished works were taken back, over all of them.</summary>
        public readonly int collected;

        /// <summary>How many waiting works were submitted to a destination, over all of them.</summary>
        public readonly int submitted;

        /// <summary>How many occasions a participant said something of its own had moved.</summary>
        public readonly int mainThreadSteps;

        /// <summary>Whether this update moved anything at all.</summary>
        public bool MadeProgress => collected > 0 || submitted > 0 || mainThreadSteps > 0;

        /// <summary>
        /// Two updates of the **same** frame, added up, for a caller that carries its frame on after doing something
        /// of its own in between. It is one frame's work reported as one number, not two frames' worth: the budget was
        /// never refilled between them.
        /// </summary>
        public SharedWorkFrameProgress Plus(in SharedWorkFrameProgress other)
        {
            return new SharedWorkFrameProgress(
                occasions + other.occasions, collected + other.collected, submitted + other.submitted,
                mainThreadSteps + other.mainThreadSteps);
        }
    }

    /// <summary>
    /// One draw frame's worth of main-thread work over one <see cref="SharedWorkDispatcher"/>: take back what the
    /// workers finished, let the participants carry those results forward — applying meshes, committing geometry,
    /// giving reservations back, making the cuts that were waiting on a commit — and submit what that made ready, then
    /// round again while anything moves.
    /// <para>
    /// **Why going round matters.** A collection is what frees a reservation, finishes a numerical result and settles
    /// a commit; the work that was waiting on any of those only becomes ready afterwards. Submitting only before the
    /// collection means the bake waits for the next frame, and so does the cut that wanted the reservation, and so
    /// does every child of a commit. Going round is not extra work: it is the same work, in the frame it became
    /// possible in.
    /// </para>
    /// <para>
    /// **The budget is the frame's, not the occasion's.** <see cref="SharedWorkDispatcher.BeginFrame"/> refills only
    /// when the frame id changes, so every occasion of one frame -- and a second <see cref="Update"/> of the same
    /// frame -- spends what is left of the one budget.
    /// </para>
    /// <para>
    /// **A spent budget ends the frame.** When the budget runs out, the participants are still pumped once more, in
    /// that same occasion: the collection the last of the budget paid for has to be carried forward, or its result
    /// would sit unprocessed and what it holds would not go back. After that this returns. **Main-thread movement on
    /// its own never begins another occasion once the budget is spent**, however much of it there is -- the next
    /// occasion could neither collect nor submit anything, so going round for it would be going round for nothing.
    /// What is left carries to the next frame.
    /// </para>
    /// <para>
    /// **It waits for nothing.** A work a worker has not finished is not waited for, completed, slept on or polled
    /// for: it is simply not collected this time. A destination with no room, a queue with no place and a budget spent
    /// all end the cycle rather than block it.
    /// </para>
    /// <para>
    /// Main thread only, and never from inside itself: a participant that called back into this from a collection or
    /// a commit would be re-entering the dispatch it is inside, which is refused.
    /// </para>
    /// </summary>
    public sealed class SharedWorkFrame
    {
        private readonly SharedWorkDispatcher _dispatcher;
        private readonly List<IMainThreadPump> _participants = new List<IMainThreadPump>(4);
        private bool _updating;

        public SharedWorkFrame(SharedWorkDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>The dispatcher this frame drives.</summary>
        public SharedWorkDispatcher Dispatcher => _dispatcher;

        /// <summary>How many participants this carries forward.</summary>
        public int ParticipantCount => _participants.Count;

        /// <summary>
        /// Adds something with main-thread work of its own. The order they are added in is the order they are pumped
        /// in, which decides who sees a collection first within one occasion and nothing else: anything that becomes
        /// ready later is taken on the next time round.
        /// </summary>
        public void Add(IMainThreadPump participant)
        {
            if (participant == null)
            {
                throw new ArgumentNullException(nameof(participant));
            }

            if (_updating)
            {
                throw new InvalidOperationException("a participant cannot be added from inside an update");
            }

            if (!_participants.Contains(participant))
            {
                _participants.Add(participant);
            }
        }

        /// <summary>Stops carrying one forward. It is not disposed or ended here; it is simply no longer pumped.</summary>
        public bool Remove(IMainThreadPump participant)
        {
            if (_updating)
            {
                throw new InvalidOperationException("a participant cannot be removed from inside an update");
            }

            return _participants.Remove(participant);
        }

        /// <summary>
        /// One frame's update: opens <paramref name="frameId"/> if it is not open already, then goes round the cycle
        /// while anything moves. Calling it again with the same id continues that frame on what is left of its budget;
        /// calling it with a new id begins a new one.
        /// </summary>
        public SharedWorkFrameProgress Update(int frameId)
        {
            if (_updating)
            {
                throw new InvalidOperationException("an update cannot be started from inside an update");
            }

            _updating = true;
            try
            {
                _dispatcher.BeginFrame(frameId);
                int occasions = 0;
                int collected = 0;
                int submitted = 0;
                int steps = 0;
                while (true)
                {
                    occasions++;

                    // Collection first, then what only the main thread can do with what came back, then whatever that
                    // made ready -- which the next time round submits.
                    DispatchProgress dispatched = _dispatcher.Dispatch();
                    collected += dispatched.collected;
                    submitted += dispatched.submitted;
                    bool moved = dispatched.MadeProgress;

                    // Always pumped, including in the occasion that spent the last of the budget: what that collection
                    // brought back is carried forward here, and nothing paid for is left unprocessed.
                    for (int p = 0; p < _participants.Count; p++)
                    {
                        if (_participants[p].Pump())
                        {
                            steps++;
                            moved = true;
                        }
                    }

                    if (!moved)
                    {
                        break;
                    }

                    if (_dispatcher.RemainingBudget <= 0)
                    {
                        // Nothing more can be collected or submitted this frame, so another occasion could only pump
                        // again. Main-thread movement alone is not a reason to take one.
                        break;
                    }
                }

                return new SharedWorkFrameProgress(occasions, collected, submitted, steps);
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
