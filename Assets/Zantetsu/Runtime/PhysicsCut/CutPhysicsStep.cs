// No observation gate in this file (DESIGN 21.18): the simulation's measured duration is the budget's input (the cost
// history the next decision reads), so a Release Player measures it as the Editor does. The diagnosis is under DEBUG.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// **The one place the default physics scene is advanced** (DESIGN 4.4): with
    /// <see cref="SimulationMode.Script"/>, a fixed step of 45 or 90 Hz chosen at start -- or, on PCVR, the display's
    /// step of 72 or 90 Hz with one double step when two are owed (CutPhysicsStep.Pcvr.cs) -- at most one
    /// <see cref="Physics.Simulate"/> per rendered frame, taken only when a whole step is owed and its expected cost fits
    /// what is left of the frame's Main budget.
    /// <para>
    /// **Where it sits, and why.** One player-loop system right after the late updates
    /// (<see cref="PreLateUpdate.ScriptRunBehaviourLateUpdate"/>). By then every driver has done its publications of
    /// this frame -- in its update and its late update -- and no snapshot has been collected yet. So the order of
    /// DESIGN 7.1.1 holds for every world at once: publication, then the decision to simulate or not, then each driver's
    /// display collects (<see cref="ProvisionalCutDriver.CollectSnapshot"/>), then the drawing. The drivers do not
    /// simulate and do not collect in their late update; this does both, in that order, once per frame.
    /// </para>
    /// <para>
    /// **Real time, and only real steps count.** The time owed is monotonic real time between frames
    /// (<see cref="ManualPhysicsClock"/>), not the clamped and scaled frame delta; a pause of the game
    /// (<see cref="SetPaused"/>) or of the application accumulates nothing. The physics time and the step id move only
    /// on a real simulation. When the project is not in script mode -- a test that switched it -- nothing is simulated
    /// here, so nothing is ever stepped twice; the collection still happens.
    /// </para>
    /// <para>
    /// **The expected cost** is the median of the last five measured durations of a real simulation
    /// (<see cref="SimulateCostHistory"/>), each also recorded on the profiler marker below: one long simulation among
    /// short ones does not hold the steps back on its own. It is not a promise of resuming: a long first simulation, or
    /// long ones making up most of the last five, still give a prediction that may never fit, and a frame that does not
    /// simulate adds no sample. The
    /// Main budget is a time, distinct from the dispatcher's count budget, and "what is left" is that time minus what
    /// the frame has already spent since its player loop began. Neither is a guarantee: a prediction can be wrong and a
    /// frame can go over, and DESIGN 4.4 accepts both. A frame whose budget is gone simply does not simulate, and the
    /// time it owed stays owed.
    /// </para>
    /// <para>
    /// **Chosen once, here, before the first simulation.** The frequency belongs to the physics scene, not to any world:
    /// it is settled when a play session starts (<see cref="ResetForSession"/>), from the start-up argument
    /// <c>-zantetsuPhysicsHz 45|90</c>, or else from the PCVR display once at the session's start (no step is taken
    /// before), or else 45 Hz. Nothing a world does later -- whenever it is loaded -- changes it.
    /// </para>
    /// <para>
    /// **The shared Main budget** (DESIGN 4.4, 2026-10-19; margin 2026-10-20) is the application's target frame period
    /// less one common margin, <see cref="CommonMarginSeconds"/> -- 9 % of that period, or the fixed 2 ms of before by
    /// <c>-zantetsuMainMargin fixed2ms</c> -- for what follows in the frame; what is left of it is that less the time
    /// since the frame's player loop began. Every Main reader -- the cuts' build and publication, this step, the spatial
    /// split, the collections, the compaction, the crowd's refill -- reads this one remainder, from the one start: no
    /// reader has a frame of its own, no reading or change of work starts it again, and a negative remainder is a
    /// shortfall as it stands. The period is settled at each frame's start from the host's source
    /// (<see cref="SetApplicationFrameSource"/>: a Quest under frame synthesis renders at 45 Hz, 22.2 ms), else from the
    /// PCVR display's rate settled at the session's start (72 or 90 Hz), and is one 90 Hz frame
    /// (<see cref="DefaultApplicationFrameSeconds"/>) when there is neither, the source has no answer, or its answer is
    /// not a frame period (<see cref="AcceptedFrameSeconds"/>). Never from measured frame times or fps.
    /// </para>
    /// </summary>
    public static partial class CutPhysicsStep
    {
        /// <summary>The physics frequency when nothing else was chosen (DESIGN 4.4: 45 Hz by default).</summary>
        public const int DefaultFrequencyHz = 45;

        /// <summary>The application's frame period taken when the host gives none (or none usable): one 90 Hz frame.</summary>
        public const double DefaultApplicationFrameSeconds = 1.0 / 90.0;

        /// <summary>
        /// The one margin the shared Main budget leaves in every frame for what follows its Main readers (the cameras'
        /// preparations and the rendering), this frame's: <see cref="MarginFor"/> of this frame's period with the
        /// session's <see cref="MarginKind"/> -- 9 % of the period (90 Hz: 1.00 ms, 72 Hz: 1.25 ms, 45 Hz: 2.00 ms), or
        /// the fixed 2 ms of 2026-10-19 for comparison. **Provisional** -- not a measured right value. Kept here alone; a
        /// reader's own reserve for the same purpose is not taken again, and none is given back for a smaller margin
        /// (<see cref="CutWorldProfile.ReserveFrameTailSeconds"/>).
        /// </summary>
        public static double CommonMarginSeconds => MarginFor(s_frameSeconds, s_marginKind);

        /// <summary>The share of the target period the margin is (TL, 2026-10-20): 9 %.</summary>
        public const double MarginFraction = 0.09;

        /// <summary>The margin of 2026-10-19 (D-251), kept for comparison by <c>-zantetsuMainMargin fixed2ms</c>: 2 ms whatever the period.</summary>
        public const double FixedMarginSeconds = 0.002;

        /// <summary>How the session's margin is taken: <see cref="MainMarginKind.PeriodFraction"/> unless the start-up argument says otherwise.</summary>
        public static MainMarginKind MarginKind => s_marginKind;

        /// <summary>The margin of a frame period with a kind (pure): 9 % of the period, or the fixed 2 ms.</summary>
        internal static double MarginFor(double frameSeconds, MainMarginKind kind) =>
            kind == MainMarginKind.Fixed2Ms ? FixedMarginSeconds : MarginFraction * frameSeconds;

        /// <summary>The shared Main budget with no source: <see cref="DefaultApplicationFrameSeconds"/> less its margin.</summary>
        public static double DefaultMainBudgetSeconds => BudgetFor(DefaultApplicationFrameSeconds);

        /// <summary>The shortest and the longest frame period a source's answer is taken as (144 Hz to 30 Hz); outside it, the default.</summary>
        public const double MinimumFrameSeconds = 1.0 / 144.0, MaximumFrameSeconds = 1.0 / 30.0;

        /// <summary>The start-up argument that chooses the physics frequency, followed by 45 or 90.</summary>
        public const string FrequencyArgument = "-zantetsuPhysicsHz";

        private static readonly ProfilerMarker s_simulate = new ProfilerMarker("Zantetsu.CutPhysicsStep.Simulate");
        private static readonly ProfilerMarker s_collect = new ProfilerMarker("Zantetsu.CutPhysicsStep.Collect");
        private static readonly List<ProvisionalCutDriver> s_collectors = new List<ProvisionalCutDriver>(2);
        private static readonly List<Action> s_afterStep = new List<Action>(2);
        private static readonly List<Action> s_afterCollect = new List<Action>(2);

        private static ManualPhysicsClock s_clock = new ManualPhysicsClock(DefaultFrequencyHz, Stopwatch.Frequency);
        private static readonly SimulateCostHistory s_costs = new SimulateCostHistory();          // normal steps only
        private static readonly SimulateCostHistory s_doubleCosts = new SimulateCostHistory();    // double steps only
        private static MainMarginKind s_marginKind = MainMarginKind.PeriodFraction;
        private static bool s_stepSettled = true;   // false only while a PCVR display step waits for its rate at the start
        private static double s_frameSeconds = DefaultApplicationFrameSeconds;   // this frame's period, settled at its start
        private static Func<double> s_frameSource;
        private static long s_frameStart;
        private static bool s_explicitPaused;      // SetPaused: a caller's own pause, ended only by that caller
        private static bool s_applicationPaused;   // the application's own pause, by the listener below
        private static bool s_gamePaused;          // the game's pause as a whole (Zantetsu.Core.GamePause: manual, application, XR focus)
        private static PauseListener s_pauseListener;

        /// <summary>The clock of this run: frequency, step, time owed, physics time and step id.</summary>
        public static ManualPhysicsClock Clock => s_clock;

        /// <summary>This frame's application frame period, in seconds, as settled at the frame's start.</summary>
        public static double ApplicationFrameSeconds => s_frameSeconds;

        /// <summary>The shared Main budget of this frame, in seconds: <see cref="ApplicationFrameSeconds"/> less <see cref="CommonMarginSeconds"/>.</summary>
        public static double MainBudgetSeconds => BudgetFor(s_frameSeconds);

        /// <summary>Whether this frame's period came from a source -- the host's or the PCVR display's settled rate (false: the default, for want of a usable answer).</summary>
        public static bool ApplicationFrameFromSource => ApplicationFrameSource != ApplicationFrameSourceKind.Default;

        /// <summary>Where this frame's period came from: the host's source (a Quest's frame synthesis), the PCVR display's settled rate, or the default.</summary>
        public static ApplicationFrameSourceKind ApplicationFrameSource { get; private set; }

        /// <summary>This frame's period source in words, for records.</summary>
        public static string ApplicationFrameSourceName => ApplicationFrameSource == ApplicationFrameSourceKind.Host ? "frame synthesis"
            : ApplicationFrameSource == ApplicationFrameSourceKind.PcvrDisplay ? "PCVR display " + s_pcvrHz + " Hz (settled at start)"
            : "default";

        /// <summary>Live remaining Main time, shared by publication and simulation; querying never refills it.</summary>
        internal static double RemainingMainSeconds => !Application.isPlaying || s_frameStart == 0
            ? double.PositiveInfinity
            : RemainingAt(s_frameSeconds, s_frameStart, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

        /// <summary>The shared budget of a frame period with the session's margin kind: the period less its margin.</summary>
        internal static double BudgetFor(double frameSeconds) => BudgetFor(frameSeconds, s_marginKind);

        /// <summary>The shared budget of a frame period with a margin kind (pure; tests read it). A double step does not change it.</summary>
        internal static double BudgetFor(double frameSeconds, MainMarginKind kind) => frameSeconds - MarginFor(frameSeconds, kind);

        /// <summary>What is left of the shared budget at <paramref name="now"/> of a frame begun at <paramref name="frameStart"/>: negative when short (pure but for the session's margin kind).</summary>
        internal static double RemainingAt(double frameSeconds, long frameStart, long now, long frequency) =>
            BudgetFor(frameSeconds) - (now - frameStart) / (double)frequency;

        /// <summary>
        /// The frame period a source's answer is taken as: the answer when it is a frame period (finite, from
        /// <see cref="MinimumFrameSeconds"/> to <see cref="MaximumFrameSeconds"/>), otherwise the default -- no answer
        /// (NaN), 0 or less, or anything else is never an extended budget. Pure.
        /// </summary>
        internal static double AcceptedFrameSeconds(double answer, out bool fromSource)
        {
            fromSource = !double.IsNaN(answer) && !double.IsInfinity(answer) && answer >= MinimumFrameSeconds && answer <= MaximumFrameSeconds;
            return fromSource ? answer : DefaultApplicationFrameSeconds;
        }

        /// <summary>
        /// The host's source of the application's target frame period, read once at each frame's start (null: none, the
        /// default from the next frame on). The Quest's demo gives the frame synthesis's period while AppSW is applied
        /// and NaN otherwise; a source that throws is taken as having no answer. Replaces the source before it.
        /// </summary>
        public static void SetApplicationFrameSource(Func<double> source) => s_frameSource = source;

        /// <summary>Clears the source only if it is still <paramref name="source"/> (a host leaving does not clear another's).</summary>
        public static void ClearApplicationFrameSource(Func<double> source)
        {
            if (s_frameSource == source) s_frameSource = null;
        }

        /// <summary>The timestamp this frame began at (0 before the first frame): for tests of the order at the frame's start.</summary>
        internal static long FrameStartTimestamp => s_frameStart;

        /// <summary>Settles the frame's period from the source (the frame's start; tests call it to stand for one).</summary>
        internal static void SettleFramePeriod()
        {
            double answer = double.NaN;
            if (s_frameSource != null)
            {
                try { answer = s_frameSource(); }
                catch (Exception error) { answer = double.NaN; UnityEngine.Debug.LogException(error); }
            }

            s_frameSeconds = AcceptedFrameSeconds(answer, out bool fromSource);
            ApplicationFrameSource = fromSource ? ApplicationFrameSourceKind.Host : ApplicationFrameSourceKind.Default;
            if (!fromSource && s_pcvrState == PcvrRateState.Obtained)
            {
                // The PCVR display's rate, settled once at the session's start: never this frame's measured length, so a
                // reprojected frame (45 or 36 fps) does not widen the budget.
                s_frameSeconds = 1.0 / s_pcvrHz;
                ApplicationFrameSource = ApplicationFrameSourceKind.PcvrDisplay;
            }
        }

        /// <summary>
        /// What is left of this frame's Main budget now, in seconds (positive infinity outside Play or before the first
        /// frame), for other Main work that must leave the frame's room to the simulation: read only, never refilled.
        /// </summary>
        public static double FrameRemainingMainSeconds => RemainingMainSeconds;

        /// <summary>The duration of the last real simulation, in seconds (the latest sample, not the prediction).</summary>
        public static double LastSimulateSeconds { get; private set; }

        /// <summary>The duration of the last frame's collection (the drivers' display snapshots after the decision), in seconds.</summary>
        public static double LastCollectSeconds { get; private set; }

        /// <summary>The expected cost of the next normal simulation: the median of the last five real normal ones, 0 before any.</summary>
        public static double ExpectedSimulateSeconds => s_costs.ExpectedSeconds;

        /// <summary>
        /// The expected cost of the next double step: the median of the last five real double steps, its own history,
        /// apart from the normal steps'. Before any (or after a re-measurement cleared it), twice the normal step's
        /// expected cost -- a conservative first estimate: one simulation of twice the time is one solver pass, and is
        /// not expected to cost more than two normal ones. Only a real double step adds a sample.
        /// </summary>
        public static double ExpectedDoubleSimulateSeconds => ExpectedDoubleFrom(s_doubleCosts, s_costs);

        /// <summary>
        /// What Main work before the decision reserves for the step, always (unlike
        /// <see cref="ExpectedStepSecondsLaterThisFrame"/>, which is 0 when no step will be owed): the normal step's
        /// expected cost, or the double step's on a clock with the double step when two steps will be owed. On the fixed
        /// step it is <see cref="ExpectedSimulateSeconds"/>, as before.
        /// </summary>
        public static double ExpectedStepReserveSeconds =>
            s_clock.DoubleStep && Application.isPlaying && s_clock.WouldOweADoubleStepAt(Stopwatch.GetTimestamp())
                ? ExpectedDoubleSimulateSeconds
                : s_costs.ExpectedSeconds;

        /// <summary>The double step's estimate from the two histories (pure): its own median, or twice the normal one with no double sample.</summary>
        internal static double ExpectedDoubleFrom(SimulateCostHistory doubles, SimulateCostHistory normals) =>
            doubles.Count > 0 ? doubles.ExpectedSeconds : 2.0 * normals.ExpectedSeconds;

        /// <summary>
        /// What this frame's decision is still expected to spend on a simulation, in seconds, for Main work that runs
        /// **before** it (a late update): the expected cost of the step the owing will make the candidate -- the double
        /// step's when two normal steps will be owed on a clock with the double step, the normal step's otherwise --
        /// when the physics is stepped here; 0 when no step will be owed, when this frame's decision has already been
        /// made, before the step is settled, or outside Play. It is the decision's own estimate, read ahead; it forbids
        /// nothing and decides nothing, and work that leaves it room does not thereby stop the steps.
        /// </summary>
        public static double ExpectedStepSecondsLaterThisFrame
        {
            get
            {
                if (!Application.isPlaying || !s_stepSettled || LastDecidedFrame == Time.frameCount || Physics.simulationMode != SimulationMode.Script)
                {
                    return 0.0;
                }

                long now = Stopwatch.GetTimestamp();
                if (!s_clock.WouldOweAStepAt(now)) return 0.0;
                return s_clock.WouldOweADoubleStepAt(now) ? ExpectedDoubleSimulateSeconds : s_costs.ExpectedSeconds;
            }
        }

        /// <summary>
        /// Whether this frame's decision, still to come, may take its verification step past the budget (a step owed,
        /// and either enough cost skips in a row that one more makes the count, or a re-evaluation asked for). Main work
        /// that may itself go past the budget once -- the spatial split's retry of an unmeasured publication -- reads
        /// it to keep the two out of one frame. A reading ahead; it decides nothing. False outside Play, once this
        /// frame's decision is made, or when not in script mode.
        /// </summary>
        public static bool VerificationStepMayComeLaterThisFrame =>
            Application.isPlaying && s_stepSettled && LastDecidedFrame != Time.frameCount && Physics.simulationMode == SimulationMode.Script
            && s_clock.WouldOweAStepAt(Stopwatch.GetTimestamp())
            && (SkippedForCostInARow + 1 >= VerifyAfterSkippedFrames || s_reevaluateAsked);

        /// <summary>The expected cost the last decision compared, in seconds: the double step's when it took one, the normal step's otherwise. For tests and observation.</summary>
        public static double LastDecisionExpectedSeconds { get; private set; }

        /// <summary>The step the last decision simulated, in seconds: 0 for none, the normal step, or twice it. For tests and observation.</summary>
        public static double LastDecisionStepSeconds { get; private set; }

        /// <summary>The step the owing made the candidate at the last decision, before the cost and the budget, in seconds (0: none owed). For tests and observation.</summary>
        public static double LastDecisionCandidateSeconds { get; private set; }

        /// <summary>Real simulations of the normal step and of the double step, and their measured durations summed, in seconds, this session.</summary>
        public static double NormalSimulateSeconds { get; private set; }

        /// <inheritdoc cref="NormalSimulateSeconds"/>
        public static double DoubleSimulateSeconds { get; private set; }

        /// <summary>The longest measured duration of a normal step and of a double step this session, in seconds.</summary>
        public static double MaxNormalSimulateSeconds { get; private set; }

        /// <inheritdoc cref="MaxNormalSimulateSeconds"/>
        public static double MaxDoubleSimulateSeconds { get; private set; }

        /// <summary>
        /// Decisions with a step owed that the budget refused (the estimate over what was left, or nothing left), before
        /// a verification step is considered; a decision that then took its verification step past the budget is
        /// counted here and in <see cref="VerificationSteps"/>.
        /// </summary>
        public static long BudgetShortDecisions { get; private set; }

        /// <summary>Decisions where two steps were owed and the double step did not fit, and the normal step was taken instead.</summary>
        public static long DoubleFellBackToNormal { get; private set; }

        /// <summary>What was left of the Main budget at the last decision, in seconds. For tests and observation.</summary>
        public static double LastDecisionRemainingSeconds { get; private set; }

        /// <summary>Whether the last decision simulated. For tests and observation.</summary>
        public static bool LastDecisionStepped { get; private set; }

        /// <summary>The frame of the last real simulation, or <see cref="int.MinValue"/>. For tests and observation.</summary>
        public static int LastSimulatedFrame { get; private set; } = int.MinValue;

        /// <summary>The frame of the last decision, simulated or not, or <see cref="int.MinValue"/>.</summary>
        public static int LastDecidedFrame { get; private set; } = int.MinValue;

        /// <summary>
        /// After this many decisions in a row that skipped for the expected cost alone (time owed, budget short of the
        /// estimate), one verification step is taken past the budget: the estimate is from real simulations only, and a
        /// world whose load fell after the estimate was taken would otherwise never be simulated again.
        /// </summary>
        public const int VerifyAfterSkippedFrames = 45;

        /// <summary>Verification steps taken: a simulation past the budget to measure the cost again (bounded: one per <see cref="VerifyAfterSkippedFrames"/> skipped frames, or one asked for).</summary>
        public static int VerificationSteps { get; private set; }

        /// <summary>The duration of the last verification step, in seconds.</summary>
        public static double LastVerificationSeconds { get; private set; }

        /// <summary>The sum, over the verification steps, of their cost beyond what was left of the budget, in seconds.</summary>
        public static double VerificationOverrunSeconds { get; private set; }

        /// <summary>The decisions in a row that skipped for the expected cost alone, so far.</summary>
        public static int SkippedForCostInARow { get; private set; }

        private static bool s_reevaluateAsked;

        /// <summary>
        /// Asks for the cost to be measured again at the next decision (a world's load was reduced: an aggregation
        /// completed, groups rested): the next decision simulates once even past the budget, and the estimate starts
        /// over from that measurement. One step, no catch-up.
        /// </summary>
        public static void RequestCostReevaluation()
        {
            s_reevaluateAsked = true;
            s_doubleReevaluateAsked = true;   // held apart: a normal step taken does not clear it, only a double candidacy
        }

        /// <summary>
        /// After this many decisions in a row where the double step was the candidate and the normal step was taken for
        /// want of room for the double step's estimate, that estimate is taken again from the normal step's (twice it).
        /// </summary>
        public const int DoubleRefreshAfterFallbacks = 45;

        /// <summary>How many times the double step's measured estimate was set aside to be taken again from the normal step's.</summary>
        public static int DoubleEstimateRefreshes { get; private set; }

        private static bool s_doubleReevaluateAsked;
        private static int s_doubleFellBackInARow;

        /// <summary>
        /// Whether this decision takes the double step's estimate again from the normal step's (pure): only when the
        /// double step is the candidate, and a re-evaluation was asked for or <see cref="DoubleRefreshAfterFallbacks"/>
        /// candidacies in a row fell back to the normal step.
        /// </summary>
        internal static bool ShouldRefreshDoubleEstimate(bool doubleCandidate, bool asked, int fellBackInARow) =>
            doubleCandidate && (asked || fellBackInARow >= DoubleRefreshAfterFallbacks);

        /// <summary>Tests only: the double step's estimate as if five real double steps had cost this.</summary>
        internal static void InjectExpectedDoubleCostForTest(double seconds)
        {
            s_doubleCosts.Clear();
            for (int i = 0; i < SimulateCostHistory.Capacity; i++) s_doubleCosts.Add(seconds);
        }

        /// <summary>Tests only: the estimate the next decisions compare with, as if five real normal simulations had cost this (no double sample: the double step's estimate is twice it).</summary>
        internal static void InjectExpectedCostForTest(double seconds)
        {
            s_costs.Clear();
            s_doubleCosts.Clear();
            for (int i = 0; i < SimulateCostHistory.Capacity; i++) s_costs.Add(seconds);
        }

        /// <summary>
        /// A caller's own pause of the step: while it holds, no time is owed to the physics. It is one of three
        /// reasons -- this, the application's pause, the game's pause as a whole -- and the step runs only while none
        /// holds; no reason's end ends another (DESIGN 4.6, D-241).
        /// </summary>
        public static void SetPaused(bool paused)
        {
            s_explicitPaused = paused;
            ApplyPause();
        }

        /// <summary>The step is paused while any reason holds: the explicit pause, the application's, the game's.</summary>
        internal static bool PauseAggregate(bool explicitPaused, bool applicationPaused, bool gamePaused) => explicitPaused || applicationPaused || gamePaused;

        /// <summary>Whether the clock stands now (tests).</summary>
        internal static bool IsClockPaused => s_clock.IsPaused;

        private static void SetGamePaused(bool paused)
        {
            s_gamePaused = paused;
            ApplyPause();
        }

        private static void ApplyPause() => s_clock.SetPaused(PauseAggregate(s_explicitPaused, s_applicationPaused, s_gamePaused));

        /// <summary>Follows the game's pause as a whole (tests; in a Player this is done at the session's start).</summary>
        internal static void FollowGamePause()
        {
            Zantetsu.Core.GamePause.PausedChanged -= SetGamePaused;
            Zantetsu.Core.GamePause.PausedChanged += SetGamePaused;
            SetGamePaused(Zantetsu.Core.GamePause.IsPaused);
        }

        /// <summary>
        /// Puts the system into the player loop once and takes part in collecting for <paramref name="driver"/>: its
        /// display collects right after this frame's decision. A driver does this while it is driving.
        /// </summary>
        internal static void Join(ProvisionalCutDriver driver)
        {
            Loop.EnsureInstalled();
            if (!s_collectors.Contains(driver))
            {
                s_collectors.Add(driver);
            }
        }

        /// <summary>Stops collecting for <paramref name="driver"/>.</summary>
        internal static void Leave(ProvisionalCutDriver driver)
        {
            s_collectors.Remove(driver);
        }

        /// <summary>
        /// Takes part in every simulated step: <paramref name="afterStep"/> is called right after a step was simulated,
        /// before that frame's collections, and in no frame without a step. What it moves is part of what the step
        /// leaves: a display that asks placements only after a new step (DESIGN 5.6, D-204) reads it, and it is no
        /// placement input changed outside a step.
        /// </summary>
        internal static void JoinStep(Action afterStep)
        {
            Loop.EnsureInstalled();
            if (!s_afterStep.Contains(afterStep))
            {
                s_afterStep.Add(afterStep);
            }
        }

        /// <summary>Takes part in the steps no more.</summary>
        internal static void LeaveStep(Action afterStep)
        {
            s_afterStep.Remove(afterStep);
        }

        /// <summary>
        /// Takes part after every frame's collections: <paramref name="afterCollect"/> is called once a frame, after
        /// the decision to simulate, the step if it was taken and every driver's collection -- so after everything
        /// this frame published, grew or compacted, and before the frame is rendered. It is for optional work that
        /// starts only in a frame that has had none of those (DESIGN 7.9.3's delayed collection); nothing it does is
        /// in this frame's snapshot.
        /// </summary>
        internal static void JoinAfterCollect(Action afterCollect)
        {
            Loop.EnsureInstalled();
            if (!s_afterCollect.Contains(afterCollect))
            {
                s_afterCollect.Add(afterCollect);
            }
        }

        /// <summary>Takes part after the collections no more.</summary>
        internal static void LeaveAfterCollect(Action afterCollect)
        {
            s_afterCollect.Remove(afterCollect);
        }

        /// <summary>
        /// A play session starts from nothing, whether or not the domain was reloaded: the frequency and the step's mode
        /// chosen for this session (a PCVR display step is settled at the first frames, CutPhysicsStep.Pcvr.cs), the
        /// margin, the budget, no time owed, no step taken, no pause, no collector left from a previous session. This
        /// runs before any scene is loaded and so before any simulation.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            s_marginKind = MarginFromCommandLine(arguments);
            int explicitHz = FrequencyFromCommandLine(arguments);
            PhysicsStepRequest request = StepRequestFromCommandLine(arguments);
            bool pc = Application.platform != RuntimePlatform.Android;
            ChooseStepAtSessionStart(pc, explicitHz, request);
            s_doubleCosts.Clear();
            NormalSimulateSeconds = DoubleSimulateSeconds = MaxNormalSimulateSeconds = MaxDoubleSimulateSeconds = 0.0;
            BudgetShortDecisions = DoubleFellBackToNormal = 0;
            DoubleEstimateRefreshes = 0;
            s_doubleReevaluateAsked = false;
            s_doubleFellBackInARow = 0;
            LastDecisionStepSeconds = LastDecisionCandidateSeconds = 0.0;
            s_frameSeconds = DefaultApplicationFrameSeconds;
            s_frameSource = null;
            ApplicationFrameSource = ApplicationFrameSourceKind.Default;
            s_frameStart = 0;
            s_explicitPaused = false;
            s_gamePaused = false;
            s_applicationPaused = false;
            s_pauseListener = null;
            s_collectors.Clear();
            s_afterStep.Clear();
            s_afterCollect.Clear();
            LastSimulateSeconds = 0.0;
            LastCollectSeconds = 0.0;
            s_costs.Clear();
            VerificationSteps = 0;
            LastVerificationSeconds = 0.0;
            VerificationOverrunSeconds = 0.0;
            SkippedForCostInARow = 0;
            s_reevaluateAsked = false;
            LastDecisionExpectedSeconds = 0.0;
            LastDecisionRemainingSeconds = 0.0;
            LastDecisionStepped = false;
            LastSimulatedFrame = int.MinValue;
            LastDecidedFrame = int.MinValue;
            ResetDiagnosisForSession();   // DIAGNOSIS ONLY: off unless ZTK_STEP_DIAG=1 (CutPhysicsStep.Diagnosis.cs)
        }

        /// <summary>
        /// The frequency given at start: <see cref="FrequencyArgument"/> followed by 45 or 90, or 0 when none is given.
        /// Anything else given there is reported once and taken as none given: the automatic choice is made.
        /// </summary>
        internal static int FrequencyFromCommandLine(string[] arguments)
        {
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (!string.Equals(arguments[i], FrequencyArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(arguments[i + 1], out int hz) && (hz == 45 || hz == 90))
                {
                    return hz;
                }

                UnityEngine.Debug.LogWarning(
                    FrequencyArgument + " takes 45 or 90; '" + arguments[i + 1] + "' is not one, so the frequency is chosen as if none were given.");
                break;
            }

            return 0;
        }

        /// <summary>
        /// The physics scene is advanced by this in every play session, with or without a cut world: with the project in
        /// script mode nothing else steps it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallForSession()
        {
            if (Zantetsu.Core.LaunchDiagnosis.EmptyXrScene)
            {
                // DIAGNOSIS ONLY (unit 22): the empty XR scene has no physics to step; the player-loop systems are not
                // installed and the scene is not simulated (script mode: nothing else steps it).
                Zantetsu.Core.LaunchDiagnosis.NoteStoodDown("physics step loop (CutPhysicsStep)");
                UnityEngine.Debug.Log("CUT PHYSICS STEP: not installed -- " + Zantetsu.Core.LaunchDiagnosis.Describe());
                return;
            }

            Loop.EnsureInstalled();

            // The game's pause as a whole (DESIGN 4.6: manual, application, XR focus) pauses the step here, beside a
            // caller's own SetPaused and the application's pause; the pause of progress itself -- no cut taken, nothing
            // published -- is each entrance's own reading of Zantetsu.Core.GamePause.
            FollowGamePause();
        }

        internal static void BeginFrame()
        {
            // The frame's one start and one period: every reader of this frame compares with these. The start first: the
            // time the source takes (the Quest's XR and OpenXR queries, an interruption in them) is the frame's own and is
            // taken from the shared remainder like any other (TL review, 2026-10-19).
            s_frameStart = Stopwatch.GetTimestamp();
            ReadPcvrDisplay(s_frameStart);   // PCVR only: the rate settled at the start, then watched (CutPhysicsStep.Pcvr.cs)
            SettleFramePeriod();
            SayPcvrSettled();
            s_gcCountAtFrameStart = GC.CollectionCount(0);
        }

        private static int s_gcCountAtFrameStart;

        /// <summary>
        /// How often something outside a physics step has changed what a placement is read from (DESIGN 5.6, D-204):
        /// an owner come or gone, a provisional pair made, handed over or ended, an owner's root put somewhere by a
        /// script. With <see cref="ManualPhysicsClock.StepId"/> it is what a display compares to know that the
        /// placements it adopted are still the ones to be read. Whoever makes such a change says so here; a change
        /// made without saying so is not seen until the next step.
        /// </summary>
        public static long PlacementInputChanges { get; private set; }

        /// <summary>
        /// Of <see cref="PlacementInputChanges"/>, the ones told with no target: something changed and what it
        /// concerns was not said. A display's held placements are all asked again for one of these (DESIGN 5.6, D-207).
        /// </summary>
        public static long UntargetedPlacementInputChanges { get; private set; }

        // The fragments the targeted changes were told with, the last ChangedKept of them, by their running number.
        private const int ChangedKept = 256;
        private static readonly Zantetsu.MeshCut.LogicalFragmentId[] s_changed = new Zantetsu.MeshCut.LogicalFragmentId[ChangedKept];
        private static long s_changedCount;

        /// <summary>A placement input was changed outside a physics step, and what it concerns is not said: every held placement is asked again.</summary>
        public static void NotePlacementInputChanged()
        {
            PlacementInputChanges++;
            UntargetedPlacementInputChanges++;
        }

        /// <summary>
        /// A placement input of <paramref name="fragment"/> was changed outside a physics step: what answers for it
        /// (an owner added, handed over, withdrawn or retired; a provisional pair made or ended), or where its owner's
        /// root was put. Any fragment of the lineage will do -- the source of a cut, a side, a piece: a display asks
        /// the held placements of that fragment's family again, and no other's (D-207). A fragment that is not set
        /// says nothing of the target and is told as <see cref="NotePlacementInputChanged()"/>.
        /// </summary>
        public static void NotePlacementInputChanged(Zantetsu.MeshCut.LogicalFragmentId fragment)
        {
            if (!fragment.IsSet)
            {
                NotePlacementInputChanged();
                return;
            }

            PlacementInputChanges++;
            s_changed[(int)(s_changedCount & (ChangedKept - 1))] = fragment;
            s_changedCount++;
        }

        /// <summary>
        /// Something through which no placement is asked yet was placed or ended -- a root just made, a side built and
        /// not published, an owner not registered. No placement anybody holds is concerned; a collection only does
        /// not let its snapshot stand for it (D-204).
        /// </summary>
        public static void NoteUnpublishedPlacementChange()
        {
            PlacementInputChanges++;
        }

        /// <summary>
        /// The fragments the targeted changes since <paramref name="cursor"/> were told with, added to
        /// <paramref name="into"/> (a reader keeps its own cursor; -1 to begin), and the count of the changes told with
        /// no target. False when more were told since the cursor than are kept: the reader is to take every placement
        /// as possibly changed. The cursor is brought to now either way.
        /// </summary>
        public static bool TryReadPlacementChanges(ref long cursor, List<Zantetsu.MeshCut.LogicalFragmentId> into, out long untargeted)
        {
            untargeted = UntargetedPlacementInputChanges;
            long told = s_changedCount - cursor;
            bool kept = cursor >= 0 && told >= 0 && told <= ChangedKept;
            if (kept)
            {
                for (long r = cursor; r < s_changedCount; r++)
                {
                    into.Add(s_changed[(int)(r & (ChangedKept - 1))]);
                }
            }

            cursor = s_changedCount;
            return kept;
        }

        /// <summary>
        /// How many garbage collections have completed since this frame began (the collector's own count of
        /// generation 0 collections; an incremental collection counts when it completes, its slices do not, and a
        /// collection later in this frame is not known yet). 0 outside Play or before the first frame.
        /// </summary>
        public static int GcCollectionsThisFrame =>
            !Application.isPlaying || s_frameStart == 0 ? 0 : GC.CollectionCount(0) - s_gcCountAtFrameStart;

        /// <summary>The decision, the simulation if it is taken, and then each driver's collection.</summary>
        private static void AfterLateUpdate()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            EnsurePauseListener();
            long now = Stopwatch.GetTimestamp();
            if (s_pcvrState == PcvrRateState.Pending)
            {
                ReadPcvrDisplay(now);   // a frame whose start this system missed: the start's reading, here
                SayPcvrSettled();
            }

            // Before the step is settled (a PCVR display step waiting for its rate at the session's start) nothing is
            // owed and nothing is simulated: the first step taken is of the settled length.
            if (s_stepSettled)
            {
                s_clock.Accumulate(now);   // the time since the last decision added, what is over the cap dropped
            }

            LastDecidedFrame = Time.frameCount;
            // A frame that began before the system was installed has no start of its own: it is taken as beginning now.
            long frameStart = s_frameStart != 0 ? s_frameStart : now;
            double remaining = RemainingAt(s_frameSeconds, frameStart, now, Stopwatch.Frequency);
            double expected = s_costs.ExpectedSeconds;
            bool scripted = Physics.simulationMode == SimulationMode.Script && s_stepSettled;
            double candidate = scripted ? s_clock.CandidateStepSeconds() : 0.0;
            // The double step's estimate is taken again from the normal step's (twice it) when a re-evaluation was asked
            // for, or after DoubleRefreshAfterFallbacks double candidacies in a row fell back to the normal step: a stale
            // high estimate would otherwise never be measured again, the normal steps going on. It is still compared with
            // the budget; nothing is simulated past it for this (TL review, 2026-10-20).
            if (ShouldRefreshDoubleEstimate(candidate > s_clock.StepSeconds, s_doubleReevaluateAsked, s_doubleFellBackInARow))
            {
                if (s_doubleCosts.Count > 0) DoubleEstimateRefreshes++;
                s_doubleCosts.Clear();
                s_doubleReevaluateAsked = false;
                s_doubleFellBackInARow = 0;
            }

            double expectedDouble = ExpectedDoubleSimulateSeconds;
            // Then the step is chosen from what is owed (ManualPhysicsClock.Choose): the double step when two normal steps
            // are owed and its own estimate fits, else the normal step when its estimate fits, else none.
            double chosen = scripted ? s_clock.Choose(expected, expectedDouble, remaining) : 0.0;
            bool step = chosen > 0.0;
            bool shouldStep = step;   // DIAGNOSIS ONLY: the plain decision, before a verification step
            if (candidate > 0.0 && !step) BudgetShortDecisions++;
            if (step && candidate > s_clock.StepSeconds && chosen == s_clock.StepSeconds)
            {
                DoubleFellBackToNormal++;
                s_doubleFellBackInARow++;
            }
            else if (step && chosen > s_clock.StepSeconds)
            {
                s_doubleFellBackInARow = 0;
            }
            // A skip for the cost alone (time owed, the estimate over what is left): counted; after enough of them in a
            // row, or when a re-evaluation was asked for, one verification step is taken past the budget so that the
            // estimate is measured again -- a stale high estimate never holds the physics for ever. One normal step,
            // never the double one, and not a catch-up.
            bool verify = false;
            if (scripted && !step && s_clock.ShouldStep(0.0, remaining) && s_clock.ShouldStep(expected, double.PositiveInfinity))
            {
                SkippedForCostInARow++;
                if (SkippedForCostInARow >= VerifyAfterSkippedFrames || s_reevaluateAsked)
                {
                    step = true;
                    verify = true;
                    chosen = s_clock.StepSeconds;
                }
            }
            else if (step)
            {
                SkippedForCostInARow = 0;
                s_reevaluateAsked = false;   // the steps go on by themselves: nothing to measure again
            }

            bool isDouble = step && chosen > s_clock.StepSeconds;
            LastDecisionExpectedSeconds = isDouble ? expectedDouble : expected;
            LastDecisionRemainingSeconds = remaining;
            LastDecisionStepped = step;
            LastDecisionStepSeconds = step ? chosen : 0.0;
            LastDecisionCandidateSeconds = candidate;
            RecordDecision(scripted, remaining, expected, shouldStep, step, verify, s_frameStart, now);   // DIAGNOSIS ONLY: nothing when off
            if (step)
            {
                long begin = Stopwatch.GetTimestamp();
                using (s_simulate.Auto())
                {
                    Physics.Simulate((float)chosen);   // one simulation: the normal step or the double one, never two
                }

                LastSimulateSeconds = (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;   // the budget's input: the histories below predict the next steps from it (kept in every build)
                if (verify)
                {
                    VerificationSteps++;
                    LastVerificationSeconds = LastSimulateSeconds;
                    VerificationOverrunSeconds += Math.Max(0.0, LastSimulateSeconds - remaining);
                    SkippedForCostInARow = 0;
                    s_reevaluateAsked = false;
                    s_costs.Clear();         // the estimate starts over from this measurement
                    s_doubleCosts.Clear();   // and the double step's from twice it, until a double step is measured
                    s_doubleReevaluateAsked = false;
                    s_doubleFellBackInARow = 0;
                }

                if (isDouble)
                {
                    s_doubleCosts.Add(LastSimulateSeconds);
                    DoubleSimulateSeconds += LastSimulateSeconds;
                    MaxDoubleSimulateSeconds = Math.Max(MaxDoubleSimulateSeconds, LastSimulateSeconds);
                }
                else
                {
                    s_costs.Add(LastSimulateSeconds);
                    NormalSimulateSeconds += LastSimulateSeconds;
                    MaxNormalSimulateSeconds = Math.Max(MaxNormalSimulateSeconds, LastSimulateSeconds);
                }

                s_clock.Stepped(chosen);   // what was simulated is consumed; the physics time moves by it, the step id by one
                s_clock.CarryOver();       // with the double step: at most two normal steps carried to the next decision
                LastSimulatedFrame = Time.frameCount;

                // What moves with the steps (JoinStep), before anything is collected. Backwards and bounds-checked, as below.
                for (int i = s_afterStep.Count - 1; i >= 0; i--)
                {
                    if (i < s_afterStep.Count)
                    {
                        s_afterStep[i]();
                    }
                }
            }
            else if (s_stepSettled)
            {
                s_clock.CarryOver();   // a decision without a step carries at most two normal steps too (nothing on the fixed step)
            }

            // Backwards and bounds-checked: a collection may end a driver (the termination latch), which leaves.
            long collectBegin = Stopwatch.GetTimestamp();
            using (s_collect.Auto())
            {
                for (int i = s_collectors.Count - 1; i >= 0; i--)
                {
                    if (i < s_collectors.Count)
                    {
                        s_collectors[i].CollectSnapshot();
                    }
                }
            }

            LastCollectSeconds = (double)(Stopwatch.GetTimestamp() - collectBegin) / Stopwatch.Frequency;

            // What starts only after everything this frame collected (JoinAfterCollect). Backwards and bounds-checked, as above.
            for (int i = s_afterCollect.Count - 1; i >= 0; i--)
            {
                if (i < s_afterCollect.Count)
                {
                    s_afterCollect[i]();
                }
            }
        }

        private static void EnsurePauseListener()
        {
            if (s_pauseListener != null)
            {
                return;
            }

            var host = new GameObject("Cut physics step (application pause)") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            s_pauseListener = host.AddComponent<PauseListener>();
        }

        /// <summary>The application's own pause (a headset taken off, the app sent to the background).</summary>
        private sealed class PauseListener : MonoBehaviour
        {
            private void OnApplicationPause(bool paused)
            {
                s_applicationPaused = paused;
                ApplyPause();
            }
        }

        /// <summary>The two places in the player loop, installed once.</summary>
        private static class Loop
        {
            private struct CutPhysicsFrameBegin
            {
            }

            private struct CutPhysicsStepAfterLateUpdate
            {
            }

            internal static void EnsureInstalled()
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null)
                {
                    return;
                }

                bool changed = false;
                for (int i = 0; i < root.subSystemList.Length; i++)
                {
                    PlayerLoopSystem stage = root.subSystemList[i];
                    if (stage.subSystemList == null)
                    {
                        continue;
                    }

                    if (stage.type == typeof(Initialization) && Find(stage, typeof(CutPhysicsFrameBegin)) < 0)
                    {
                        var systems = new List<PlayerLoopSystem>(stage.subSystemList);
                        systems.Insert(0, new PlayerLoopSystem { type = typeof(CutPhysicsFrameBegin), updateDelegate = BeginFrame });
                        stage.subSystemList = systems.ToArray();
                        root.subSystemList[i] = stage;
                        changed = true;
                    }
                    else if (stage.type == typeof(PreLateUpdate) && Find(stage, typeof(CutPhysicsStepAfterLateUpdate)) < 0)
                    {
                        int after = Find(stage, typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate));
                        if (after < 0)
                        {
                            continue;
                        }

                        var systems = new List<PlayerLoopSystem>(stage.subSystemList);
                        systems.Insert(after + 1, new PlayerLoopSystem
                        {
                            type = typeof(CutPhysicsStepAfterLateUpdate),
                            updateDelegate = AfterLateUpdate,
                        });
                        stage.subSystemList = systems.ToArray();
                        root.subSystemList[i] = stage;
                        changed = true;
                    }
                }

                if (changed)
                {
                    PlayerLoop.SetPlayerLoop(root);
                }
            }

            private static int Find(PlayerLoopSystem stage, Type type)
            {
                for (int s = 0; s < stage.subSystemList.Length; s++)
                {
                    if (stage.subSystemList[s].type == type)
                    {
                        return s;
                    }
                }

                return -1;
            }
        }
    }
}
