using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A crowd's one refill queue on the Main thread, shared by all its individuals (no per-individual coroutine): the
    /// display starts of individuals a published plan has and no slot yet carries, and the re-preparation of returned
    /// slots. Each frame <see cref="Run"/> goes through them in order -- starts first, oldest first, then one
    /// re-preparation at a time -- and runs a stage only when its expected cost fits both the refill's own cap for the
    /// frame and what is left of the frame's Main budget over a reserve. A stage whose expected cost is over the cap by
    /// itself is not run at all (<see cref="OverCapRefusals"/>): it is to be split or prepared ahead, not forced.
    /// <para>
    /// The decision before a stage (from the expected cost) and what the stage then measured are recorded apart: a
    /// stage that went over what it was allowed shows as an overrun, never as a changed decision.
    /// </para>
    /// <para>
    /// A start is requested once per individual: asking again replaces its plan (the latest published one) and keeps its
    /// place and waiting time. A start the crowd no longer plans is cancelled; <see cref="CancelAll"/> ends every waiting
    /// one (the world's end). Main thread only.
    /// </para>
    /// </summary>
    public sealed class MobPlanRefillQueue<TPlan>
    {
        /// <summary>
        /// A display start; a returned slot's preparation again; and, before it only when the slot's kept baked collider
        /// meshes no longer match its input, their baking as a stage of its own (never inside a preparation).
        /// </summary>
        public enum Stage { Start = 0, Prepare = 1, Bake = 2 }

        /// <summary>What a start did: started, found no free slot (it waits), or was dropped (its plan no longer holds).</summary>
        public enum StartResult { Started, NoSlot, Dropped }

        private sealed class Pending
        {
            public int id;
            public TPlan plan;
            public int frame;
            public long timestamp;
        }

        // A stage's expected cost: the median of its last five measured runs, the window starting full of the seed -- so
        // one slow run does not move it, and only a cost that stays over the cap has the stage refused.
        private sealed class Cost
        {
            private readonly double[] _samples = new double[5];
            private readonly double[] _sorted = new double[5];
            private int _next;

            public Cost(double seed)
            {
                for (int i = 0; i < _samples.Length; i++) _samples[i] = seed;
            }

            public double Expected
            {
                get
                {
                    Array.Copy(_samples, _sorted, _samples.Length);
                    Array.Sort(_sorted);
                    return _sorted[_samples.Length / 2];
                }
            }

            public void Add(double seconds)
            {
                _samples[_next] = seconds;
                _next = (_next + 1) % _samples.Length;
            }
        }

        /// <summary>One run of a stage: the decision's inputs and what it measured.</summary>
        public struct Record
        {
            public int frame;
            public Stage stage;
            public double expected, capLeft, remaining, measured, overrun, waitSeconds;
            public int waitFrames;
        }

        private readonly List<Pending> _starts = new List<Pending>();
        private readonly Cost[] _costs;
        private readonly Func<long> _clock;
        private readonly double _frequency;

        public MobPlanRefillQueue(double startSeedSeconds, double prepareSeedSeconds, double bakeSeedSeconds, Func<long> clock = null, double frequency = 0)
        {
            _costs = new[] { new Cost(startSeedSeconds), new Cost(prepareSeedSeconds), new Cost(bakeSeedSeconds) };
            _clock = clock ?? Stopwatch.GetTimestamp;
            _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        }

        /// <summary>What ran, oldest first; the caller reads and clears it.</summary>
        public readonly List<Record> Records = new List<Record>();

        public int WaitingStarts => _starts.Count;
        public int Requests { get; private set; }
        public int Replaced { get; private set; }
        public int Cancelled { get; private set; }
        public int Dropped { get; private set; }
        public int Started { get; private set; }
        public int Prepared { get; private set; }
        public int Baked { get; private set; }

        /// <summary>The most starts that waited at once (read at each run and each request).</summary>
        public int MaxWaitingStarts { get; private set; }
        public int DeferredByCap { get; private set; }
        public int DeferredByFrame { get; private set; }
        public int OverCapRefusals { get; private set; }
        public int Overruns { get; private set; }
        public int NoSlotFrames { get; private set; }

        public double ExpectedSeconds(Stage stage) => _costs[(int)stage].Expected;

        public bool IsWaiting(int id) => IndexOf(id) >= 0;

        public bool TryGetPlan(int id, out TPlan plan)
        {
            int at = IndexOf(id);
            plan = at >= 0 ? _starts[at].plan : default;
            return at >= 0;
        }

        /// <summary>The ids waiting to start, oldest first.</summary>
        public IEnumerable<KeyValuePair<int, TPlan>> Waiting
        {
            get
            {
                foreach (Pending p in _starts) yield return new KeyValuePair<int, TPlan>(p.id, p.plan);
            }
        }

        /// <summary>Asks for an individual's start; an individual already waiting only takes the new plan. True when new.</summary>
        public bool Request(int id, TPlan plan, int frame)
        {
            int at = IndexOf(id);
            if (at >= 0)
            {
                _starts[at].plan = plan;
                Replaced++;
                return false;
            }

            _starts.Add(new Pending { id = id, plan = plan, frame = frame, timestamp = _clock() });
            Requests++;
            MaxWaitingStarts = Math.Max(MaxWaitingStarts, _starts.Count);
            return true;
        }

        /// <summary>Cancels a waiting start (the crowd no longer plans the individual). True when one was waiting.</summary>
        public bool Cancel(int id)
        {
            int at = IndexOf(id);
            if (at < 0) return false;
            _starts.RemoveAt(at);
            Cancelled++;
            return true;
        }

        /// <summary>Cancels every waiting start (the world's end); nothing more starts.</summary>
        public void CancelAll()
        {
            Cancelled += _starts.Count;
            _starts.Clear();
        }

        /// <summary>
        /// One frame's refill. <paramref name="start"/> starts an individual with its current plan -- it takes the slot,
        /// or says there is none, or drops a plan that no longer holds without taking one; <paramref name="hasPrepare"/>
        /// says whether a returned slot is ready to be prepared again and <paramref name="prepare"/> prepares one;
        /// <paramref name="needsBake"/> says whether that slot must first have its collider meshes baked again, which
        /// <paramref name="bake"/> does as a stage of its own. <paramref name="remainingSeconds"/> is what is left of the
        /// frame's Main budget, read before each stage.
        /// </summary>
        public void Run(int frame, double capSeconds, Func<double> remainingSeconds, double reserveSeconds,
            Func<int, TPlan, StartResult> start, Func<bool> hasPrepare, Func<bool> prepare, Func<bool> needsBake = null, Func<bool> bake = null)
        {
            MaxWaitingStarts = Math.Max(MaxWaitingStarts, _starts.Count);
            double used = 0;
            while (_starts.Count > 0)
            {
                if (!Decide(Stage.Start, frame, capSeconds, used, remainingSeconds, reserveSeconds, out double expected, out double remaining)) return;
                Pending p = _starts[0];
                long began = _clock();
                StartResult result = start(p.id, p.plan);
                double measured = (_clock() - began) / _frequency;
                used += measured;
                if (result == StartResult.NoSlot)
                {
                    NoSlotFrames++;
                    break;
                }

                _starts.RemoveAt(0);
                if (result == StartResult.Dropped)
                {
                    Dropped++;
                    continue;
                }

                Started++;
                Note(Stage.Start, frame, expected, capSeconds - (used - measured), remaining, reserveSeconds, measured, frame - p.frame, (began - p.timestamp) / _frequency);
            }

            while (hasPrepare())
            {
                Stage stage = needsBake != null && bake != null && needsBake() ? Stage.Bake : Stage.Prepare;
                if (!Decide(stage, frame, capSeconds, used, remainingSeconds, reserveSeconds, out double expected, out double remaining)) return;
                long began = _clock();
                bool ok = stage == Stage.Bake ? bake() : prepare();
                double measured = (_clock() - began) / _frequency;
                used += measured;
                if (ok && stage == Stage.Bake) Baked++;
                else if (ok) Prepared++;
                Note(stage, frame, expected, capSeconds - (used - measured), remaining, reserveSeconds, measured, 0, 0);
                if (stage == Stage.Bake && !ok) return;   // a bake that failed is not tried again at once
            }
        }

        // The decision before a stage, from its expected cost only.
        private bool Decide(Stage stage, int frame, double cap, double used, Func<double> remainingSeconds, double reserve, out double expected, out double remaining)
        {
            expected = _costs[(int)stage].Expected;
            remaining = remainingSeconds();
            if (expected > cap) { OverCapRefusals++; return false; }
            if (used + expected > cap) { DeferredByCap++; return false; }
            if (remaining - reserve < expected) { DeferredByFrame++; return false; }
            return true;
        }

        private void Note(Stage stage, int frame, double expected, double capLeft, double remaining, double reserve, double measured, int waitFrames, double waitSeconds)
        {
            _costs[(int)stage].Add(measured);
            double allowed = Math.Min(capLeft, remaining - reserve);
            double overrun = measured > allowed ? measured - allowed : 0;
            if (overrun > 0) Overruns++;
            Records.Add(new Record
            {
                frame = frame, stage = stage, expected = expected, capLeft = capLeft, remaining = remaining, measured = measured,
                overrun = overrun, waitFrames = waitFrames, waitSeconds = waitSeconds,
            });
        }

        private int IndexOf(int id)
        {
            for (int i = 0; i < _starts.Count; i++) if (_starts[i].id == id) return i;
            return -1;
        }
    }
}
