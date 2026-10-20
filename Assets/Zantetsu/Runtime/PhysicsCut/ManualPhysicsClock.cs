using System;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The arithmetic of the manual physics update of DESIGN 4.4, apart from Unity: a fixed step chosen once, the real
    /// time that has not been simulated yet, and the decision whether this frame may simulate once.
    /// <para>
    /// **Real time, not game time.** What is accumulated is monotonic elapsed time between calls to
    /// <see cref="Accumulate"/>, never the engine's frame delta: that one is clamped to a maximum and scaled by the
    /// game's time scale, and DESIGN 4.4 asks for neither. Time while paused is not accumulated and nothing is stepped
    /// while paused, and a start or a resume only sets where the next interval begins. What is not simulated stays,
    /// however long it has grown, and a pause keeps it for after the resume.
    /// </para>
    /// <para>
    /// **At most once per decision, and only on an ask.** <see cref="ShouldStep"/> and <see cref="Choose"/> say yes when
    /// a whole step is owed and the step is expected to fit what is left of the frame's Main budget; they never say
    /// "twice" and never force one after being refused. Only <see cref="Stepped(double)"/> -- told after a real
    /// simulation -- consumes what it simulated and advances the physics time and the step id by it: a frame that decided
    /// not to simulate advances neither.
    /// </para>
    /// <para>
    /// **The double step (PCVR, DESIGN 4.4, 2026-10-20).** A clock made with <c>doubleStep</c> may take, once, a single
    /// simulation of twice the normal step when two steps are owed -- one <c>Physics.Simulate(2h)</c>, never two of
    /// <c>h</c> -- if its own expected cost fits. **Its cap is on what a decision carries over** (TL review, 2026-10-20):
    /// at most two normal steps go from one decision to the next (<see cref="CarryOver"/>, applied again at the next
    /// <see cref="Accumulate"/> before the new time is added), and what is over is dropped and counted. The order of one
    /// decision: the carried time (at most 2h) plus the time since, the choice from all of it (<see cref="Choose"/>: at
    /// most 2h is simulated), then the carry-over. A remainder under the cap is kept, so frames a little shorter than 2h
    /// (an application just over half the display's rate) lose no time; after a stall the first decision simulates at most 2h and carries at most 2h on, so the catch-up stays bounded (more than 2h in all, never unbounded). (With
    /// the cap before the choice, as first written, such frames alternated h and 2h and dropped about a quarter of the
    /// time.) A clock without the double step is the fixed step of before: the cap at <see cref="Accumulate"/>, one
    /// normal step at a time.
    /// </para>
    /// </summary>
    public sealed class ManualPhysicsClock
    {
        private readonly long _ticksPerSecond;
        private long _last;
        private bool _started;
        private bool _paused;

        /// <param name="frequencyHz">The physics frequency, 45, 72 or 90 (DESIGN 4.4).</param>
        /// <param name="ticksPerSecond">The frequency of the monotonic clock the timestamps come from.</param>
        /// <param name="doubleStep">Whether a decision may take one step of twice the normal step when two are owed (PCVR's display step).</param>
        public ManualPhysicsClock(int frequencyHz, long ticksPerSecond, bool doubleStep = false)
        {
            if (!IsSupportedFrequency(frequencyHz))
            {
                throw new ArgumentOutOfRangeException(nameof(frequencyHz), "the physics frequency is 45, 72 or 90 Hz");
            }

            if (ticksPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            }

            FrequencyHz = frequencyHz;
            StepSeconds = 1.0 / frequencyHz;
            DoubleStep = doubleStep;
            _ticksPerSecond = ticksPerSecond;
        }

        /// <summary>The physics frequency chosen for this run: the normal step's.</summary>
        public int FrequencyHz { get; }

        /// <summary>The normal step, the inverse of <see cref="FrequencyHz"/>. A double step is twice it.</summary>
        public double StepSeconds { get; }

        /// <summary>Whether a decision may take one double step when two normal steps are owed.</summary>
        public bool DoubleStep { get; }

        /// <summary>Normal steps and double steps really simulated (their sum is <see cref="StepId"/>).</summary>
        public long NormalSteps { get; private set; }

        /// <inheritdoc cref="NormalSteps"/>
        public long DoubleSteps { get; private set; }

        /// <summary>The length of the last step really simulated, in seconds (0 before any).</summary>
        public double LastStepSeconds { get; private set; }

        /// <summary>
        /// Real time owed to the physics and not simulated yet, in seconds. On the fixed step at most
        /// <see cref="MaxOwedSteps"/> steps' worth; with the double step, at most that much is carried from one decision
        /// to the next (<see cref="CarryOver"/>), and at a decision it is that plus the time since.
        /// </summary>
        public double UnsimulatedSeconds { get; private set; }

        /// <summary>
        /// How many steps' worth of time may be owed at once (on the fixed step) or carried from one decision to the next
        /// (with the double step). What is beyond it is dropped (and counted in <see cref="DroppedSeconds"/>): when the
        /// steps resume they resume at the step's pace, not as an unbounded catch-up of every step missed.
        /// </summary>
        public const int MaxOwedSteps = 2;

        /// <summary>Owed time dropped at the cap, in seconds, over the run.</summary>
        public double DroppedSeconds { get; private set; }

        /// <summary>
        /// The physics time: the sum of the lengths of the steps really simulated. With double steps it is not
        /// <see cref="StepId"/> times <see cref="StepSeconds"/>: a reader of elapsed physics time reads this.
        /// </summary>
        public double PhysicsSeconds { get; private set; }

        /// <summary>
        /// How many steps have really been simulated -- the Global FixedStepId of DESIGN 4.4: one per real
        /// simulation, a double step as one. A count of updates, not of time.
        /// </summary>
        public long StepId { get; private set; }

        /// <summary>Whether time is not being accumulated.</summary>
        public bool IsPaused => _paused;

        /// <summary>The frequencies DESIGN 4.4 allows: 45 or 90 chosen at start, 72 or 90 as a PCVR display's step.</summary>
        public static bool IsSupportedFrequency(int frequencyHz)
        {
            return frequencyHz == 45 || frequencyHz == 72 || frequencyHz == 90;
        }

        /// <summary>
        /// Adds the real time since the previous call, unless paused. The first call after the start or a resume only
        /// sets the point the next interval begins at. A clock that went backwards adds nothing. On the fixed step what
        /// is over the cap is dropped after the time is added; with the double step the cap is on what was carried
        /// over, applied before the time is added (<see cref="CarryOver"/>, again here in case the last decision did not).
        /// </summary>
        public void Accumulate(long nowTicks)
        {
            if (_paused)
            {
                return;
            }

            if (DoubleStep)
            {
                CarryOver();
            }

            if (!_started)
            {
                _started = true;
                _last = nowTicks;
                return;
            }

            long elapsed = nowTicks - _last;
            _last = nowTicks;
            if (elapsed > 0)
            {
                UnsimulatedSeconds += (double)elapsed / _ticksPerSecond;
                if (!DoubleStep)
                {
                    double cap = MaxOwedSteps * StepSeconds;
                    if (UnsimulatedSeconds > cap)
                    {
                        DroppedSeconds += UnsimulatedSeconds - cap;
                        UnsimulatedSeconds = cap;
                    }
                }
            }
        }

        /// <summary>
        /// The end of a decision on a clock with the double step: what is still owed is carried to the next decision up
        /// to <see cref="MaxOwedSteps"/> normal steps, and what is over is dropped and counted. Nothing on the fixed step
        /// (its cap is at <see cref="Accumulate"/>), nothing more when called again.
        /// </summary>
        public void CarryOver()
        {
            if (!DoubleStep)
            {
                return;
            }

            double cap = MaxOwedSteps * StepSeconds;
            if (UnsimulatedSeconds > cap)
            {
                DroppedSeconds += UnsimulatedSeconds - cap;
                UnsimulatedSeconds = cap;
            }
        }

        // What the next decision will find owed if the time up to nowTicks is added (as Accumulate would add it).
        private double OwedAt(long nowTicks)
        {
            double owed = DoubleStep ? Math.Min(UnsimulatedSeconds, MaxOwedSteps * StepSeconds) : UnsimulatedSeconds;
            if (_started && nowTicks > _last)
            {
                owed += (double)(nowTicks - _last) / _ticksPerSecond;
            }

            return DoubleStep ? owed : Math.Min(owed, MaxOwedSteps * StepSeconds);
        }

        /// <summary>
        /// Stops or resumes the accumulation. Resuming realigns the interval, so the paused time is never owed; what
        /// was owed before the pause is kept.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (paused == _paused)
            {
                return;
            }

            _paused = paused;
            if (!paused)
            {
                _started = false;
            }
        }

        /// <summary>
        /// Whether this frame may simulate one step: not paused, a whole step is owed, and the expected cost fits what
        /// is left of the frame's Main budget. It changes nothing. **A pause stops the steps, not only the owing**: time
        /// owed before the pause -- left over by a frame without room, say -- is kept and is not simulated while paused.
        /// </summary>
        public bool ShouldStep(double expectedSimulateSeconds, double remainingMainSeconds)
        {
            return !_paused && UnsimulatedSeconds >= StepSeconds && expectedSimulateSeconds <= remainingMainSeconds;
        }

        /// <summary>
        /// Whether a whole step would be owed once the time up to <paramref name="nowTicks"/> had been accumulated:
        /// what this frame's decision will find of the owing, asked before it. It changes nothing -- the interval is
        /// not moved and nothing is added -- and says nothing of the cost or the budget, which the decision compares.
        /// </summary>
        public bool WouldOweAStepAt(long nowTicks)
        {
            return !_paused && OwedAt(nowTicks) >= StepSeconds;
        }

        /// <summary>
        /// The step this decision may take, in seconds: 0 for none, <see cref="StepSeconds"/>, or twice it (only with
        /// <see cref="DoubleStep"/>). Nothing when paused or less than a normal step is owed. With two normal steps owed
        /// the double step is the candidate and is taken when <paramref name="expectedDoubleSeconds"/> fits
        /// <paramref name="remainingMainSeconds"/>; when it does not, the normal step is taken if its own
        /// <paramref name="expectedNormalSeconds"/> fits (one simulation either way). With one to two owed, the normal
        /// step when it fits. It changes nothing.
        /// </summary>
        public double Choose(double expectedNormalSeconds, double expectedDoubleSeconds, double remainingMainSeconds)
        {
            if (_paused || UnsimulatedSeconds < StepSeconds)
            {
                return 0.0;
            }

            if (DoubleStep && UnsimulatedSeconds >= 2.0 * StepSeconds && expectedDoubleSeconds <= remainingMainSeconds)
            {
                return 2.0 * StepSeconds;
            }

            return expectedNormalSeconds <= remainingMainSeconds ? StepSeconds : 0.0;
        }

        /// <summary>
        /// The step the owing alone would make the candidate, before any cost or budget: 0, <see cref="StepSeconds"/>,
        /// or twice it with <see cref="DoubleStep"/> and two steps owed. Changes nothing.
        /// </summary>
        public double CandidateStepSeconds()
        {
            if (_paused || UnsimulatedSeconds < StepSeconds)
            {
                return 0.0;
            }

            return DoubleStep && UnsimulatedSeconds >= 2.0 * StepSeconds ? 2.0 * StepSeconds : StepSeconds;
        }

        /// <summary>
        /// Whether two normal steps would be owed once the time up to <paramref name="nowTicks"/> had been accumulated
        /// (as <see cref="Accumulate"/> would: the carried time capped, then the time since): the double step's
        /// candidacy, asked before the decision. False without <see cref="DoubleStep"/> or when paused. Changes nothing.
        /// </summary>
        public bool WouldOweADoubleStepAt(long nowTicks)
        {
            return !_paused && DoubleStep && OwedAt(nowTicks) >= 2.0 * StepSeconds;
        }

        /// <summary>One normal step has really been simulated (<see cref="Stepped(double)"/> with <see cref="StepSeconds"/>).</summary>
        public void Stepped()
        {
            Stepped(StepSeconds);
        }

        /// <summary>
        /// A step of <paramref name="seconds"/> has really been simulated: that much of what is owed is consumed, the
        /// physics time moves by it, and the step id by one. A normal step or, with <see cref="DoubleStep"/>, a double one.
        /// </summary>
        public void Stepped(double seconds)
        {
            bool isDouble = DoubleStep && Math.Abs(seconds - (2.0 * StepSeconds)) < 1e-12;
            if (!isDouble && Math.Abs(seconds - StepSeconds) >= 1e-12)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "a step is the normal step, or twice it on a clock with the double step");
            }

            UnsimulatedSeconds -= seconds;
            PhysicsSeconds += seconds;
            LastStepSeconds = seconds;
            StepId++;
            if (isDouble) DoubleSteps++; else NormalSteps++;
        }
    }
}
