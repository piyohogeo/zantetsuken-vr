using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The urgent destination of DESIGN 4.3: the Unity Job System, as the shared dispatch uses it. Work submitted
    /// here begins on the main thread, where it schedules its own job, and is asked without blocking whether it has
    /// finished.
    /// <para>
    /// **Unity's own workers are left alone.** Nothing here changes the worker count or any thread's priority, and
    /// nothing here claims a Unity worker: this destination's capacity bounds only the work the dispatch has handed
    /// it and not yet taken back. Work at the two pools is counted at those pools and never here.
    /// </para>
    /// <para>
    /// **Kicking the job belongs to the work.** A work that schedules a job and never kicks it can stay incomplete
    /// until the main thread forces it, which this destination never does in the ordinary path — so a work here
    /// should do what a job normally does (<c>JobHandle.ScheduleBatchedJobs</c>) and leave the eventual
    /// <c>Complete</c> to its own <see cref="IDispatchWork.Collect"/>. That is also what lets a normal stop confirm
    /// completion instead of forcing it.
    /// </para>
    /// <para>
    /// Main thread only, like the rest of the dispatch.
    /// </para>
    /// </summary>
    public sealed class UnityJobWorkExecutor : IWorkExecutor
    {
        private struct HeldWork
        {
            public IDispatchWork work;
            public Exception beginFailure;
        }

        private readonly List<HeldWork> _held = new List<HeldWork>();

        private bool _closed;

        /// <param name="capacity">
        /// How many works this destination may hold at once: begun and not yet collected. A bound, not a starting
        /// size — when it is reached, the dispatch submits nothing more here until something is collected.
        /// </param>
        public UnityJobWorkExecutor(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "must be positive");
            }

            Capacity = capacity;
        }

        public WorkDestination Destination => WorkDestination.UnityJob;

        public int Capacity { get; }

        public int Held => _held.Count;

        public bool CanAccept => !_closed && _held.Count < Capacity;

        /// <summary>
        /// Takes the work into this destination's keeping. Nothing is begun here, so nothing the work does can throw
        /// out of this; refusing is all this can do besides accepting, and a refusal leaves the work untouched.
        /// </summary>
        public bool TryAccept(IDispatchWork work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (_closed || _held.Count >= Capacity)
            {
                return false;
            }

            _held.Add(new HeldWork { work = work });
            return true;
        }

        /// <summary>
        /// Begins work already accepted here, on this thread: this is where its job is scheduled. An exception out
        /// of the work's own <see cref="IDispatchWork.Begin"/> is not caught — it reaches the caller, because
        /// whether a job was submitted before it threw cannot be known from here. The work stays held either way,
        /// is never begun again, and is collected once when it reports finished, carrying that exception as its
        /// failure.
        /// </summary>
        public void BeginAccepted(IDispatchWork work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (IndexOf(work) < 0)
            {
                throw new InvalidOperationException("this destination has not accepted that work");
            }

            try
            {
                work.Begin();
            }
            catch (Exception failure)
            {
                Remember(work, failure);
                throw;
            }
        }

        public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
        {
            for (int i = 0; i < _held.Count; i++)
            {
                HeldWork held = _held[i];

                // Asked, never waited for — and never bypassed. Work that has not finished is not handed over, at
                // any point, a stop that timed out included: it stays held, with everything it owns.
                if (!held.work.IsComplete)
                {
                    continue;
                }

                _held.RemoveAt(i);
                work = held.work;
                completion = held.beginFailure == null
                    ? WorkCompletion.Finished
                    : WorkCompletion.Failed(held.beginFailure);
                return true;
            }

            work = null;
            completion = default;
            return false;
        }

        public void CloseForNewWork()
        {
            _closed = true;
        }

        /// <summary>
        /// The normal stop. There are no workers of our own to stop here, so what is confirmed is that every job
        /// already begun has finished: it is polled until it has, or until the timeout.
        /// <para>
        /// A timeout returns false and changes nothing else: the work that has not finished stays held, with
        /// everything it owns, and is **not** offered for collection. Only what has finished is collectable, here as
        /// everywhere else. The caller confirms the stop again later — this may be called as often as needed — and
        /// the rest is then collected once, when it really has finished. Nothing forces a job to complete to get
        /// past a timeout.
        /// </para>
        /// </summary>
        public bool StopAndConfirm(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "must not be negative");
            }

            _closed = true;
            var elapsed = Stopwatch.StartNew();
            while (!AllComplete())
            {
                if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    return false;
                }

                // A stop may wait; an ordinary frame never comes here.
                Thread.Sleep(1);
            }

            return true;
        }

        private bool AllComplete()
        {
            for (int i = 0; i < _held.Count; i++)
            {
                if (!_held[i].work.IsComplete)
                {
                    return false;
                }
            }

            return true;
        }

        private int IndexOf(IDispatchWork work)
        {
            for (int i = 0; i < _held.Count; i++)
            {
                if (ReferenceEquals(_held[i].work, work))
                {
                    return i;
                }
            }

            return -1;
        }

        private void Remember(IDispatchWork work, Exception failure)
        {
            int index = IndexOf(work);
            if (index < 0)
            {
                return;
            }

            HeldWork held = _held[index];
            held.beginFailure = failure;
            _held[index] = held;
        }
    }
}
