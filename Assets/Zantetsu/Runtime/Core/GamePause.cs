using System;
using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// The pause of the game's progress, and the clocks that leave paused time out (DESIGN 4.6). Pure: its three
    /// sources -- the frame's unscaled time, the real time and the frame number -- are given, so that it is tested
    /// without the engine. <see cref="GamePause"/> is the one instance the game reads.
    /// <para>
    /// **Two causes, kept apart.** The manual pause is the player's (the menu); the system pause is the application's
    /// (the headset taken off, the app sent to the background). Progress is paused while either holds, and one ending
    /// never ends the other: a headset put back on does not close a menu the player opened.
    /// </para>
    /// <para>
    /// **The clocks.** While paused the pausable clocks stand at the value they had when the pause began, and after it
    /// they go on from that value: what was measured against them -- a script's deadline, a budget, a flight -- neither
    /// runs out during a pause nor jumps after it. The frames go on being rendered during a manual pause, so its end
    /// is taken at once, within the frame. A system pause's end is taken at the first frame that begins after the
    /// application says so: the frame time is brought up only there, and that frame, which spans the whole absence, is
    /// counted as paused (its delta reads zero).
    /// </para>
    /// </summary>
    public sealed class GamePauseState
    {
        private readonly Func<double> _unscaledNow;
        private readonly Func<double> _realtimeNow;
        private readonly Func<int> _frame;

        private bool _manual;
        private bool _system;
        private bool _systemLeaving;
        private int _systemLeaveFrame;
        private int _zeroDeltaFrame = int.MinValue;
        private double _pausedUnscaled;
        private double _pausedRealtime;
        private double _frozenUnscaled;
        private double _frozenRealtime;
        private int _pausedFrames;
        private int _frozenFrame;

        public GamePauseState(Func<double> unscaledNow, Func<double> realtimeNow, Func<int> frame)
        {
            _unscaledNow = unscaledNow ?? throw new ArgumentNullException(nameof(unscaledNow));
            _realtimeNow = realtimeNow ?? throw new ArgumentNullException(nameof(realtimeNow));
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        }

        /// <summary>Raised when progress stops (true) and when it goes on again (false), never twice in a row with the same value.</summary>
        public event Action<bool> PausedChanged;

        /// <summary>Raised when the manual pause begins (true) and ends (false), whatever the system pause is.</summary>
        public event Action<bool> ManualChanged;

        /// <summary>The player's pause.</summary>
        public bool Manual => _manual;

        /// <summary>The application's pause, until the first frame after it ended has begun.</summary>
        public bool System
        {
            get
            {
                Refresh();
                return _system;
            }
        }

        public bool IsPaused
        {
            get
            {
                Refresh();
                return _manual || _system;
            }
        }

        /// <summary>Whether any pause has happened since this state was made: until then every clock is the engine's own.</summary>
        public bool EverPaused { get; private set; }

        public int ManualPauses { get; private set; }
        public int SystemPauses { get; private set; }

        public void SetManual(bool paused)
        {
            Refresh();
            if (_manual == paused)
            {
                return;
            }

            bool before = _manual || _system;
            _manual = paused;
            if (paused)
            {
                ManualPauses++;
            }

            Apply(before);
            ManualChanged?.Invoke(paused);
        }

        public void SetSystem(bool paused)
        {
            Refresh();
            if (paused)
            {
                // Paused again before the frame that would have taken the end: it never ended.
                _systemLeaving = false;
                if (_system)
                {
                    return;
                }

                bool before = _manual || _system;
                _system = true;
                SystemPauses++;
                Apply(before);
                return;
            }

            if (_system && !_systemLeaving)
            {
                _systemLeaving = true;
                _systemLeaveFrame = _frame();
            }
        }

        /// <summary>
        /// Takes a system pause's end once a frame has begun after it. Every reading does this first; a listener that
        /// calls it at each frame's beginning makes the frame it happens in the same for every reader.
        /// </summary>
        public void Refresh()
        {
            if (!_systemLeaving || _frame() <= _systemLeaveFrame)
            {
                return;
            }

            bool before = _manual || _system;
            _systemLeaving = false;
            _system = false;
            _zeroDeltaFrame = _frame();
            Apply(before);
        }

        private void Apply(bool before)
        {
            bool now = _manual || _system;
            if (now == before)
            {
                return;
            }

            if (now)
            {
                EverPaused = true;
                _frozenUnscaled = _unscaledNow() - _pausedUnscaled;
                _frozenRealtime = _realtimeNow() - _pausedRealtime;
                _frozenFrame = _frame() - _pausedFrames;
            }
            else
            {
                _pausedUnscaled = _unscaledNow() - _frozenUnscaled;
                _pausedRealtime = _realtimeNow() - _frozenRealtime;
                _pausedFrames = _frame() - _frozenFrame;
            }

            PausedChanged?.Invoke(now);
        }

        /// <summary>The frame's unscaled time with every pause left out.</summary>
        public double UnscaledTime
        {
            get
            {
                Refresh();
                return _manual || _system ? _frozenUnscaled : _unscaledNow() - _pausedUnscaled;
            }
        }

        /// <summary>The real time with every pause left out.</summary>
        public double Realtime
        {
            get
            {
                Refresh();
                return _manual || _system ? _frozenRealtime : _realtimeNow() - _pausedRealtime;
            }
        }

        /// <summary>
        /// The frame number with the paused frames left out: it stands while paused and goes on by one a frame after
        /// it, so a wait counted in frames is not used up by a pause. (The frame a pause began in and the frame it
        /// ended in read as one.)
        /// </summary>
        public int Frame
        {
            get
            {
                Refresh();
                return _manual || _system ? _frozenFrame : _frame() - _pausedFrames;
            }
        }

        /// <summary>
        /// This frame's unscaled delta as progress may use it: none while paused, and none in the frame that took a
        /// system pause's end, whose delta is the absence itself.
        /// </summary>
        public float UnscaledDelta(float engineDelta)
        {
            Refresh();
            return _manual || _system || _frame() == _zeroDeltaFrame ? 0f : engineDelta;
        }

        /// <summary>The real seconds paused so far, the pause going on now included.</summary>
        public double PausedSeconds
        {
            get
            {
                Refresh();
                return _manual || _system ? _realtimeNow() - _frozenRealtime : _pausedRealtime;
            }
        }
    }

    /// <summary>
    /// The game's pause (DESIGN 4.6): the one state every entrance of the game's progress reads, and the clocks a
    /// script or a deadline reads in place of the engine's so that a pause is no part of them.
    /// <para>
    /// **What it is not.** It disables nothing and ends nothing: a world, its driver and its drawing go on being
    /// called, and each entrance of progress -- taking a cut, publishing, committing geometry, the optional
    /// collections, the crowd, the scripts -- stands still by reading <see cref="IsPaused"/>. Work already handed to
    /// a worker goes on and its result waits where finished results wait. The physics step is paused beside it by
    /// <c>CutPhysicsStep.SetPaused</c>, which follows <see cref="ManualChanged"/>.
    /// </para>
    /// <para>
    /// **The time scale.** While paused <see cref="Time.timeScale"/> is zero, so that everything on the engine's
    /// scaled time -- the crowd's plan time, the poses -- stands where it was; the value it had is put back when the
    /// pause ends. That alone is not the pause: what reads unscaled or real time, and what reads no time at all, stops
    /// at its own entrance.
    /// </para>
    /// <para>
    /// **The clocks.** <see cref="UnscaledTime"/>, <see cref="UnscaledTimeAsDouble"/>, <see cref="RealtimeSinceStartup"/>,
    /// <see cref="RealtimeSinceStartupAsDouble"/>, <see cref="UnscaledDeltaTime"/> and <see cref="FrameCount"/> are the
    /// engine's members of the same names with the paused time and frames left out. Until the first pause of a session
    /// they return the engine's own values, bit for bit, so a run that is never paused reads what it always read.
    /// </para>
    /// <para>
    /// **A script's frame.** A scripted run waits a frame by <c>yield return null</c> and then stays there while the
    /// game is paused (<c>while (GamePause.IsPaused) yield return null;</c>), so no stage of a script goes on during a
    /// pause; what it counts in frames it counts by <see cref="FrameCount"/>. A frame's own number, as a record or as
    /// an identity other parts compare, stays the engine's.
    /// </para>
    /// <para>
    /// **An ending is not a script's stage.** From where a run's ending begins -- what it captured being saved, its
    /// world released, its records closed, its completion told, the Player ended -- a frame's wait is the plain
    /// <c>yield return null</c>: it only spreads the work over frames, and none of it waits for the pause to end. So
    /// are the loops that only watch for an ending.
    /// </para>
    /// </summary>
    public static class GamePause
    {
        private static GamePauseState s_state = NewState();
        private static bool s_timeScaleHeld;
        private static float s_timeScaleBefore = 1f;
        private static Listener s_listener;

        private static GamePauseState NewState()
        {
            var state = new GamePauseState(
                () => Time.unscaledTimeAsDouble, () => Time.realtimeSinceStartupAsDouble, () => Time.frameCount);
            state.PausedChanged += OnPausedChanged;
            state.ManualChanged += paused => ManualChanged?.Invoke(paused);
            return state;
        }

        /// <summary>The manual pause began (true) or ended (false).</summary>
        public static event Action<bool> ManualChanged;

        /// <summary>Progress stopped (true) or went on again (false).</summary>
        public static event Action<bool> PausedChanged;

        public static bool IsPaused => s_state.IsPaused;
        public static bool Manual => s_state.Manual;
        public static bool System => s_state.System;
        public static bool EverPaused => s_state.EverPaused;
        public static int ManualPauses => s_state.ManualPauses;
        public static int SystemPauses => s_state.SystemPauses;
        public static double PausedSeconds => s_state.PausedSeconds;

        public static void SetManual(bool paused) => s_state.SetManual(paused);
        public static void SetSystem(bool paused) => s_state.SetSystem(paused);

        public static float UnscaledTime => s_state.EverPaused ? (float)s_state.UnscaledTime : Time.unscaledTime;
        public static double UnscaledTimeAsDouble => s_state.EverPaused ? s_state.UnscaledTime : Time.unscaledTimeAsDouble;
        public static float RealtimeSinceStartup => s_state.EverPaused ? (float)s_state.Realtime : Time.realtimeSinceStartup;
        public static double RealtimeSinceStartupAsDouble => s_state.EverPaused ? s_state.Realtime : Time.realtimeSinceStartupAsDouble;
        public static float UnscaledDeltaTime => s_state.EverPaused ? s_state.UnscaledDelta(Time.unscaledDeltaTime) : Time.unscaledDeltaTime;
        public static int FrameCount => s_state.EverPaused ? s_state.Frame : Time.frameCount;

        /// <summary>One line for a run's record: whether it was paused, how often and for how long.</summary>
        public static string Describe()
        {
            return s_state.EverPaused
                ? "paused " + (s_state.ManualPauses + s_state.SystemPauses) + " time(s) (manual " + s_state.ManualPauses + ", system " + s_state.SystemPauses
                  + "), " + s_state.PausedSeconds.ToString("F2", global::System.Globalization.CultureInfo.InvariantCulture) + " s in all"
                  + (IsPaused ? ", paused now" : "") + ": NOT a performance sample"
                : "never paused";
        }

        private static void OnPausedChanged(bool paused)
        {
            if (paused)
            {
                if (!s_timeScaleHeld)
                {
                    s_timeScaleBefore = Time.timeScale;
                    s_timeScaleHeld = true;
                }

                Time.timeScale = 0f;
            }
            else if (s_timeScaleHeld)
            {
                Time.timeScale = s_timeScaleBefore;
                s_timeScaleHeld = false;
            }

            PausedChanged?.Invoke(paused);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            // A session that ended paused leaves no pause and no zero time scale to the next one.
            if (s_timeScaleHeld)
            {
                Time.timeScale = s_timeScaleBefore;
                s_timeScaleHeld = false;
            }

            s_state = NewState();
            ManualChanged = null;
            PausedChanged = null;
            s_listener = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallForSession()
        {
            if (s_listener != null)
            {
                return;
            }

            var host = new GameObject("Zantetsu game pause listener") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            s_listener = host.AddComponent<Listener>();
        }

        /// <summary>For tests: ends every pause and forgets the totals, as a new session would.</summary>
        internal static void ResetForTest()
        {
            if (s_timeScaleHeld)
            {
                Time.timeScale = s_timeScaleBefore;
                s_timeScaleHeld = false;
            }

            Action<bool> manual = ManualChanged;
            bool wasManual = s_state.Manual;
            s_state = NewState();
            if (wasManual)
            {
                manual?.Invoke(false);
            }
        }

        /// <summary>
        /// The application's own pause is the system pause; and each frame's beginning takes a system pause's end, so
        /// that every reader of the frame sees the same state.
        /// </summary>
        [DefaultExecutionOrder(-32000)]
        private sealed class Listener : MonoBehaviour
        {
            private void OnApplicationPause(bool paused) => SetSystem(paused);

            private void Update() => s_state.Refresh();
        }
    }
}
