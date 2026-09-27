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
    public static class CutPhysicsStep
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

        /// <summary>The duration of the last real simulation, in seconds (the latest sample, not the prediction).</summary>
        public static double LastSimulateSeconds { get; private set; }

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
            LastSimulateSeconds = 0.0;
            s_costs.Clear();
            LastDecisionExpectedSeconds = 0.0;
            LastDecisionRemainingSeconds = 0.0;
            LastDecisionStepped = false;
            LastSimulatedFrame = int.MinValue;
            LastDecidedFrame = int.MinValue;
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
        }

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
            bool step = Physics.simulationMode == SimulationMode.Script && s_clock.ShouldStep(expected, remaining);
            LastDecisionExpectedSeconds = expected;
            LastDecisionRemainingSeconds = remaining;
            LastDecisionStepped = step;
            if (step)
            {
                long begin = Stopwatch.GetTimestamp();
                using (s_simulate.Auto())
                {
                    Physics.Simulate((float)s_clock.StepSeconds);
                }

                LastSimulateSeconds = (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
                s_costs.Add(LastSimulateSeconds);
                s_clock.Stepped();
                LastSimulatedFrame = Time.frameCount;
            }

            // Backwards and bounds-checked: a collection may end a driver (the termination latch), which leaves.
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
