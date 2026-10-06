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
    /// <see cref="SimulationMode.Script"/>, a fixed step of 45 or 90 Hz chosen at start, at most one
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
    /// **Chosen once, here, before the first simulation.** The frequency and the budget belong to the physics scene,
    /// not to any world: they are settled when a play session starts (<see cref="ResetForSession"/>), from the start-up
    /// argument <c>-zantetsuPhysicsHz 45|90</c> or else 45 Hz, and the budget is <see cref="DefaultMainBudgetSeconds"/>.
    /// Nothing a world does later -- whenever it is loaded -- changes either.
    /// </para>
    /// </summary>
    public static partial class CutPhysicsStep
    {
        /// <summary>The physics frequency when nothing else was chosen (DESIGN 4.4: 45 Hz by default).</summary>
        public const int DefaultFrequencyHz = 45;

        /// <summary>The frame's Main budget: one 90 Hz frame. A provisional value, to be adjusted from measurement.</summary>
        public const double DefaultMainBudgetSeconds = 1.0 / 90.0;

        /// <summary>The start-up argument that chooses the physics frequency, followed by 45 or 90.</summary>
        public const string FrequencyArgument = "-zantetsuPhysicsHz";

        private static readonly ProfilerMarker s_simulate = new ProfilerMarker("Zantetsu.CutPhysicsStep.Simulate");
        private static readonly ProfilerMarker s_collect = new ProfilerMarker("Zantetsu.CutPhysicsStep.Collect");
        private static readonly List<ProvisionalCutDriver> s_collectors = new List<ProvisionalCutDriver>(2);
        private static readonly List<Action> s_afterStep = new List<Action>(2);

        private static ManualPhysicsClock s_clock = new ManualPhysicsClock(DefaultFrequencyHz, Stopwatch.Frequency);
        private static readonly SimulateCostHistory s_costs = new SimulateCostHistory();
        private static double s_mainBudgetSeconds = DefaultMainBudgetSeconds;
        private static long s_frameStart;
        private static bool s_gamePaused;
        private static bool s_applicationPaused;
        private static PauseListener s_pauseListener;

        /// <summary>The clock of this run: frequency, step, time owed, physics time and step id.</summary>
        public static ManualPhysicsClock Clock => s_clock;

        /// <summary>The frame's Main budget the decision compares with, in seconds.</summary>
        public static double MainBudgetSeconds => s_mainBudgetSeconds;

        /// <summary>Live remaining Main time, shared by publication and simulation; querying never refills it.</summary>
        internal static double RemainingMainSeconds => !Application.isPlaying || s_frameStart == 0
            ? double.PositiveInfinity
            : s_mainBudgetSeconds - (Stopwatch.GetTimestamp() - s_frameStart) / (double)Stopwatch.Frequency;

        /// <summary>
        /// What is left of this frame's Main budget now, in seconds (positive infinity outside Play or before the first
        /// frame), for other Main work that must leave the frame's room to the simulation: read only, never refilled.
        /// </summary>
        public static double FrameRemainingMainSeconds => RemainingMainSeconds;

        /// <summary>The duration of the last real simulation, in seconds (the latest sample, not the prediction).</summary>
        public static double LastSimulateSeconds { get; private set; }

        /// <summary>The duration of the last frame's collection (the drivers' display snapshots after the decision), in seconds.</summary>
        public static double LastCollectSeconds { get; private set; }

        /// <summary>The expected cost of the next simulation: the median of the last five real ones, 0 before any.</summary>
        public static double ExpectedSimulateSeconds => s_costs.ExpectedSeconds;

        /// <summary>The expected cost the last decision compared, in seconds. For tests and observation.</summary>
        public static double LastDecisionExpectedSeconds { get; private set; }

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
        }

        /// <summary>Tests only: the estimate the next decisions compare with, as if five real simulations had cost this.</summary>
        internal static void InjectExpectedCostForTest(double seconds)
        {
            s_costs.Clear();
            for (int i = 0; i < SimulateCostHistory.Capacity; i++) s_costs.Add(seconds);
        }

        /// <summary>The game's pause: while it holds, no time is owed to the physics.</summary>
        public static void SetPaused(bool paused)
        {
            s_gamePaused = paused;
            s_clock.SetPaused(s_gamePaused || s_applicationPaused);
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
        /// A play session starts from nothing, whether or not the domain was reloaded: the frequency chosen for this
        /// session, the budget, no time owed, no step taken, no pause, no collector left from a previous session. This
        /// runs before any scene is loaded and so before any simulation.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            s_clock = new ManualPhysicsClock(FrequencyFromCommandLine(), Stopwatch.Frequency);
            s_mainBudgetSeconds = DefaultMainBudgetSeconds;
            s_frameStart = 0;
            s_gamePaused = false;
            s_applicationPaused = false;
            s_pauseListener = null;
            s_collectors.Clear();
            s_afterStep.Clear();
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
        /// The frequency chosen at start: <see cref="FrequencyArgument"/> followed by 45 or 90, or else
        /// <see cref="DefaultFrequencyHz"/>. Anything else given there is reported once and the default is used.
        /// </summary>
        private static int FrequencyFromCommandLine()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (!string.Equals(arguments[i], FrequencyArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(arguments[i + 1], out int hz) && ManualPhysicsClock.IsSupportedFrequency(hz))
                {
                    return hz;
                }

                UnityEngine.Debug.LogWarning(
                    FrequencyArgument + " takes 45 or 90; '" + arguments[i + 1] + "' is not one, so "
                    + DefaultFrequencyHz + " Hz is used.");
                break;
            }

            return DefaultFrequencyHz;
        }

        /// <summary>
        /// The physics scene is advanced by this in every play session, with or without a cut world: with the project in
        /// script mode nothing else steps it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallForSession()
        {
            Loop.EnsureInstalled();
        }

        private static void BeginFrame()
        {
            s_frameStart = Stopwatch.GetTimestamp();
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
            s_clock.Accumulate(now);
            LastDecidedFrame = Time.frameCount;
            // A frame that began before the system was installed has no start of its own: it is taken as beginning now.
            long frameStart = s_frameStart != 0 ? s_frameStart : now;
            double remaining = s_mainBudgetSeconds - ((double)(now - frameStart) / Stopwatch.Frequency);
            double expected = s_costs.ExpectedSeconds;
            bool scripted = Physics.simulationMode == SimulationMode.Script;
            bool step = scripted && s_clock.ShouldStep(expected, remaining);
            bool shouldStep = step;   // DIAGNOSIS ONLY: the plain decision, before a verification step
            // A skip for the cost alone (time owed, the estimate over what is left): counted; after enough of them in a
            // row, or when a re-evaluation was asked for, one verification step is taken past the budget so that the
            // estimate is measured again -- a stale high estimate never holds the physics for ever. One step, not a catch-up.
            bool verify = false;
            if (scripted && !step && s_clock.ShouldStep(0.0, remaining) && s_clock.ShouldStep(expected, double.PositiveInfinity))
            {
                SkippedForCostInARow++;
                if (SkippedForCostInARow >= VerifyAfterSkippedFrames || s_reevaluateAsked)
                {
                    step = true;
                    verify = true;
                }
            }
            else if (step)
            {
                SkippedForCostInARow = 0;
                s_reevaluateAsked = false;   // the steps go on by themselves: nothing to measure again
            }

            LastDecisionExpectedSeconds = expected;
            LastDecisionRemainingSeconds = remaining;
            LastDecisionStepped = step;
            RecordDecision(scripted, remaining, expected, shouldStep, step, verify, s_frameStart, now);   // DIAGNOSIS ONLY: nothing when off
            if (step)
            {
                long begin = Stopwatch.GetTimestamp();
                using (s_simulate.Auto())
                {
                    Physics.Simulate((float)s_clock.StepSeconds);
                }

                LastSimulateSeconds = (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
                if (verify)
                {
                    VerificationSteps++;
                    LastVerificationSeconds = LastSimulateSeconds;
                    VerificationOverrunSeconds += Math.Max(0.0, LastSimulateSeconds - remaining);
                    SkippedForCostInARow = 0;
                    s_reevaluateAsked = false;
                    s_costs.Clear();   // the estimate starts over from this measurement
                }

                s_costs.Add(LastSimulateSeconds);
                s_clock.Stepped();
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
                s_clock.SetPaused(s_gamePaused || s_applicationPaused);
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
