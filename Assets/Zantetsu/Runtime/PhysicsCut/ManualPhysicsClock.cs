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
    /// **At most once per decision, and only on an ask.** <see cref="ShouldStep"/> says yes when a whole step is owed
    /// and the step is expected to fit what is left of the frame's Main budget; it never says "twice", never enlarges
    /// the step and never forces one after being refused. Only <see cref="Stepped"/> -- told after a real simulation --
    /// consumes a step and advances the physics time and the step id: a frame that decided not to simulate advances
    /// neither.
    /// </para>
    /// </summary>
    public sealed class ManualPhysicsClock
    {
        private readonly long _ticksPerSecond;
        private long _last;
        private bool _started;
        private bool _paused;

        /// <param name="frequencyHz">The physics frequency, 45 or 90 (DESIGN 4.4).</param>
        /// <param name="ticksPerSecond">The frequency of the monotonic clock the timestamps come from.</param>
        public ManualPhysicsClock(int frequencyHz, long ticksPerSecond)
        {
            if (!IsSupportedFrequency(frequencyHz))
            {
                throw new ArgumentOutOfRangeException(nameof(frequencyHz), "the physics frequency is 45 or 90 Hz");
            }

            if (ticksPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            }

            FrequencyHz = frequencyHz;
            StepSeconds = 1.0 / frequencyHz;
            _ticksPerSecond = ticksPerSecond;
        }

        /// <summary>The physics frequency chosen for this run.</summary>
        public int FrequencyHz { get; }

        /// <summary>The fixed step, the inverse of <see cref="FrequencyHz"/>.</summary>
        public double StepSeconds { get; }

        /// <summary>Real time owed to the physics and not simulated yet, in seconds.</summary>
        public double UnsimulatedSeconds { get; private set; }

        /// <summary>The physics time: steps really simulated, times the step.</summary>
        public double PhysicsSeconds { get; private set; }

        /// <summary>How many steps have really been simulated -- the Global FixedStepId of DESIGN 4.4.</summary>
        public long StepId { get; private set; }

        /// <summary>Whether time is not being accumulated.</summary>
        public bool IsPaused => _paused;

        /// <summary>The frequencies DESIGN 4.4 allows to be chosen at start.</summary>
        public static bool IsSupportedFrequency(int frequencyHz)
        {
            return frequencyHz == 45 || frequencyHz == 90;
        }

        /// <summary>
        /// Adds the real time since the previous call, unless paused. The first call after the start or a resume only
        /// sets the point the next interval begins at. A clock that went backwards adds nothing.
        /// </summary>
        public void Accumulate(long nowTicks)
        {
            if (_paused)
            {
                return;
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
            }
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

        /// <summary>One step has really been simulated: it is consumed, and the physics time and the step id move.</summary>
        public void Stepped()
        {
            UnsimulatedSeconds -= StepSeconds;
            PhysicsSeconds += StepSeconds;
            StepId++;
        }
    }
}
