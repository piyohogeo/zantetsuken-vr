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
    /// <summary>
    /// Why the game is paused (DESIGN 4.6, D-241): each reason is held on its own and ended on its own, and the game
    /// is paused while any holds. Manual is the player's (the menu, the consent dialog); Application is the
    /// application's own pause (OnApplicationPause: the app sent to the background, the headset asleep); Focus is the
    /// XR session's input focus lost (the system UI over the game). Application and Focus are the system reasons.
    /// </summary>
    [Flags]
    public enum GamePauseReason
    {
        None = 0,
        Manual = 1,
        Application = 2,
        Focus = 4,
    }

    public sealed class GamePauseState
    {
        private const GamePauseReason SystemReasons = GamePauseReason.Application | GamePauseReason.Focus;

        private readonly Func<double> _unscaledNow;
        private readonly Func<double> _realtimeNow;
        private readonly Func<int> _frame;

        private bool _manual;
        private GamePauseReason _systemHeld;      // the system reasons holding now
        private GamePauseReason _systemLeaving;   // the system reasons whose end waits for the next frame
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

        /// <summary>
        /// Raised when the system pause begins (true: the first system reason held) and ends (false: the last system
        /// reason's end taken), whatever the manual pause is -- so a menu already holding the game sees the system's
        /// interruption too, which <see cref="PausedChanged"/> would not tell it.
        /// </summary>
        public event Action<bool> SystemChanged;

        /// <summary>The player's pause.</summary>
        public bool Manual => _manual;

        /// <summary>The system pause -- the application's, or the XR focus lost -- until the first frame after its last reason ended has begun.</summary>
        public bool System
        {
            get
            {
                Refresh();
                return _systemHeld != GamePauseReason.None;
            }
        }

        /// <summary>Every reason holding now (a system reason stays held until the frame after its end).</summary>
        public GamePauseReason Reasons
        {
            get
            {
                Refresh();
                return (_manual ? GamePauseReason.Manual : GamePauseReason.None) | _systemHeld;
            }
        }

        public bool IsPaused
        {
            get
            {
                Refresh();
                return _manual || _systemHeld != GamePauseReason.None;
            }
        }

        /// <summary>
        /// The reasons requested now, as the requests stand: a system reason whose end waits for its frame is not
        /// among them. Read without taking any waiting end -- for a log, which must advance nothing.
        /// </summary>
        public GamePauseReason Requested => (_manual ? GamePauseReason.Manual : GamePauseReason.None) | (_systemHeld & ~_systemLeaving);

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

            bool before = _manual || _systemHeld != GamePauseReason.None;
            _manual = paused;
            if (paused)
            {
                ManualPauses++;
            }

            Apply(before);
            ManualChanged?.Invoke(paused);
        }

        /// <summary>The application's own pause (OnApplicationPause): the Application reason.</summary>
        public bool SetSystem(bool paused) => SetSystemReason(GamePauseReason.Application, paused);

        /// <summary>The XR session's input focus lost (true) or back (false): the Focus reason.</summary>
        public bool SetFocusLost(bool lost) => SetSystemReason(GamePauseReason.Focus, lost);

        /// <summary>
        /// Holds or ends one system reason. Holding one that holds already changes nothing (a repeated notification);
        /// holding one whose end was waiting for the next frame cancels that end (it never ended). An end is taken at
        /// the first frame after it, by <see cref="Refresh"/>, and only the last reason's end ends the system pause:
        /// the reasons' order of ending does not matter, and no reason's end is undone by another's.
        /// <para>
        /// Returns whether the reason's request changed: held anew (or held again while its end waited), or let go
        /// while it was held. A repeated hold, a repeated release, or the release of a reason not held is false. The
        /// request is read before anything else, so that telling it takes no waiting end first.
        /// </para>
        /// </summary>
        public bool SetSystemReason(GamePauseReason reason, bool paused)
        {
            if ((reason & SystemReasons) == 0 || (reason & (reason - 1)) != 0) throw new ArgumentException("one system reason: Application or Focus", nameof(reason));
            bool requestedBefore = (_systemHeld & reason) != 0 && (_systemLeaving & reason) == 0;
            if (paused == requestedBefore)
            {
                // Nothing of the request changes; the frame's refresh is still owed (a repeated hold took it before).
                if (paused)
                {
                    Refresh();
                }

                return false;
            }

            if (paused)
            {
                // Paused again before the frame that would have taken the end -- or in that very frame, before the
                // frame's refresh took it: it never ended. The cancel comes before the refresh, so that a reason told
                // in the frame after its end's frame neither resumes nor pauses anything: it holds on. (The observers
                // of a reason run before the frame's first refresh: XrFocusPause before GamePause's own listener.)
                _systemLeaving &= ~reason;
                if ((_systemHeld & reason) != 0)
                {
                    Refresh();
                    return true;   // held again: the waiting end is cancelled
                }

                // A reason held while another's end waits for its frame: the new reason is in the set before that end
                // is taken, so the pause goes on from one reason to the other with no resumption between them and no
                // notification but the first pause's.
                bool before = _manual || _systemHeld != GamePauseReason.None;
                bool systemBefore = _systemHeld != GamePauseReason.None;
                _systemHeld |= reason;
                if (!systemBefore)
                {
                    SystemPauses++;
                }

                Apply(before);
                if (!systemBefore)
                {
                    SystemChanged?.Invoke(true);
                }

                Refresh();
                return true;
            }

            Refresh();
            _systemLeaving |= reason;
            _systemLeaveFrame = _frame();
            return true;
        }

        /// <summary>
        /// Takes a system reason's end once a frame has begun after it. Every reading does this first; a listener that
        /// calls it at each frame's beginning makes the frame it happens in the same for every reader. While another
        /// system reason still holds, the pause goes on: the frame that ends the last one is the one with no progress.
        /// </summary>
        public void Refresh()
        {
            if (_systemLeaving == GamePauseReason.None || _frame() <= _systemLeaveFrame)
            {
                return;
            }

            bool before = _manual || _systemHeld != GamePauseReason.None;
            _systemHeld &= ~_systemLeaving;
            _systemLeaving = GamePauseReason.None;
            if (_systemHeld != GamePauseReason.None)
            {
                return;   // another system reason holds on: nothing ends
            }

            _zeroDeltaFrame = _frame();
            Apply(before);
            SystemChanged?.Invoke(false);
        }

        /// <summary>For tests: the next frame has begun, but its refresh has not run yet (a waiting end is now takeable).</summary>
        internal void BeginNextFrameForTest()
        {
            if (_systemLeaving == GamePauseReason.None) return;
            _systemLeaveFrame = _frame() - 1;
        }

        /// <summary>For tests: takes every waiting system end now, as the next frame's beginning would.</summary>
        internal void TakeSystemEndsForTest()
        {
            if (_systemLeaving == GamePauseReason.None) return;
            _systemLeaveFrame = _frame() - 1;
            Refresh();
        }

        private void Apply(bool before)
        {
            bool now = _manual || _systemHeld != GamePauseReason.None;
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
                return _manual || _systemHeld != GamePauseReason.None ? _frozenUnscaled : _unscaledNow() - _pausedUnscaled;
            }
        }

        /// <summary>The real time with every pause left out.</summary>
        public double Realtime
        {
            get
            {
                Refresh();
                return _manual || _systemHeld != GamePauseReason.None ? _frozenRealtime : _realtimeNow() - _pausedRealtime;
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
                return _manual || _systemHeld != GamePauseReason.None ? _frozenFrame : _frame() - _pausedFrames;
            }
        }

        /// <summary>
        /// This frame's unscaled delta as progress may use it: none while paused, and none in the frame that took a
        /// system pause's end, whose delta is the absence itself.
        /// </summary>
        public float UnscaledDelta(float engineDelta)
        {
            Refresh();
            return _manual || _systemHeld != GamePauseReason.None || _frame() == _zeroDeltaFrame ? 0f : engineDelta;
        }

        /// <summary>The real seconds paused so far, the pause going on now included.</summary>
        public double PausedSeconds
        {
            get
            {
                Refresh();
                return _manual || _systemHeld != GamePauseReason.None ? _realtimeNow() - _frozenRealtime : _pausedRealtime;
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
            state.SystemChanged += paused => { Log("system pause " + (paused ? "began" : "ended")); SystemChanged?.Invoke(paused); };
            return state;
        }

        /// <summary>The manual pause began (true) or ended (false).</summary>
        public static event Action<bool> ManualChanged;

        /// <summary>Progress stopped (true) or went on again (false).</summary>
        public static event Action<bool> PausedChanged;

        /// <summary>The system pause (the application's, or the XR focus lost) began (true) or ended (false), whatever the manual pause is.</summary>
        public static event Action<bool> SystemChanged;

        public const string LogPrefix = "GAME PAUSE: ";

        public static bool IsPaused => s_state.IsPaused;
        public static bool Manual => s_state.Manual;
        public static bool System => s_state.System;
        public static GamePauseReason Reasons => s_state.Reasons;
        /// <summary>The reasons requested now, read without taking a waiting end (for a record that must advance nothing).</summary>
        public static GamePauseReason Requested => s_state.Requested;
        public static bool EverPaused => s_state.EverPaused;
        public static int ManualPauses => s_state.ManualPauses;
        public static int SystemPauses => s_state.SystemPauses;
        public static double PausedSeconds => s_state.PausedSeconds;

        public static void SetManual(bool paused) => s_state.SetManual(paused);
        /// <summary>The application's pause (the Application reason); true when the request changed (and was logged).</summary>
        public static bool SetSystem(bool paused) => SetSystemReason(GamePauseReason.Application, paused);

        /// <summary>The XR focus lost or back (the Focus reason); true when the request changed (and was logged).</summary>
        public static bool SetFocusLost(bool lost) => SetSystemReason(GamePauseReason.Focus, lost);

        // Every reason's own request is one log line too (never a frame): a reason that comes or goes while another
        // holds changes nothing of the progress, and the lines of the progress alone could not tell it. The setter
        // says whether the request changed; the log reads the requests as they stand and takes no waiting end, so
        // that a reason told in the frame after its end's frame holds on (never a resumption before the hold).
        private static bool SetSystemReason(GamePauseReason reason, bool paused)
        {
            if (!s_state.SetSystemReason(reason, paused))
            {
                return false;
            }

            Debug.Log(LogPrefix + "reason " + reason + (paused ? " held" : " released (its end is taken at the next frame)") + "; requested now=" + s_state.Requested + " (frame " + Time.frameCount + ")");
            return true;
        }

        // One line a transition, never a frame: what holds now, and what progress does.
        private static void Log(string what)
        {
            Debug.Log(LogPrefix + what + "; reasons=" + s_state.Reasons + "; paused=" + s_state.IsPaused + " (frame " + Time.frameCount + ")");
        }

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

            Log("progress " + (paused ? "paused" : "resumed"));
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
            SystemChanged = null;
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

        /// <summary>For tests: the next frame has begun and its refresh has not run yet.</summary>
        internal static void BeginNextFrameForTest() => s_state.BeginNextFrameForTest();

        /// <summary>For tests: takes the waiting system ends now, as the next frame would (the manual pause and the totals stay).</summary>
        internal static void TakeSystemEndsForTest() => s_state.TakeSystemEndsForTest();

        /// <summary>For tests: ends every pause and forgets the totals, as a new session would.</summary>
        internal static void ResetForTest()
        {
            if (s_timeScaleHeld)
            {
                Time.timeScale = s_timeScaleBefore;
                s_timeScaleHeld = false;
            }

            Action<bool> manual = ManualChanged;
            Action<bool> system = SystemChanged;
            Action<bool> paused = PausedChanged;
            bool wasManual = s_state.Manual;
            bool wasSystem = s_state.System;
            bool wasPaused = s_state.IsPaused;
            s_state = NewState();
            if (wasManual)
            {
                manual?.Invoke(false);
            }

            if (wasSystem)
            {
                system?.Invoke(false);   // a listener holding for the system pause lets go, as the frame after its end would make it
            }

            if (wasPaused)
            {
                paused?.Invoke(false);   // and what follows the progress (the physics step) goes on, as the resumption would make it
            }
        }

        /// <summary>
        /// The application's own pause is the system pause; and each frame's beginning takes a system pause's end, so
        /// that every reader of the frame sees the same state.
        /// </summary>
        /// <summary>The listener's order: an observer of a reason (XrFocusPause) runs before it, so that a reason told in the frame after its end's frame cancels the end before this refresh would take it.</summary>
        public const int ListenerExecutionOrder = -32000;

        [DefaultExecutionOrder(ListenerExecutionOrder)]
        private sealed class Listener : MonoBehaviour
        {
            private void OnApplicationPause(bool paused) => SetSystem(paused);

            private void Update() => s_state.Refresh();
        }
    }
}
