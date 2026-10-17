using UnityEngine;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// The start-up work that has to end before ordinary play begins (DESIGN 21.18): a count of the parts of a scene
    /// still preparing -- a crowd making its pool and loading its plan, a placed cuttable not yet a cut target. A part
    /// says <see cref="Begin"/> when it starts to prepare and <see cref="End"/> once, when it has finished or has given
    /// up. The play statistics count no frame while <see cref="Pending"/> is above zero and play has not yet begun:
    /// the frames of the preparation are not play, however long they are. Nothing here measures or decides anything
    /// of the game; it is one integer.
    /// </summary>
    public static class PlayPreparation
    {
        /// <summary>The parts still preparing.</summary>
        public static int Pending { get; private set; }

        /// <summary>The parts that began to prepare, and that ended, since the session began.</summary>
        public static long Begun { get; private set; }
        public static long Ended { get; private set; }

        public static void Begin()
        {
            Pending++;
            Begun++;
        }

        public static void End()
        {
            if (Pending > 0) Pending--;
            Ended++;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetForSession()
        {
            Pending = 0;
            Begun = 0;
            Ended = 0;
        }
    }

    /// <summary>
    /// Where ordinary play begins for the statistics (DESIGN 21.18), apart from Unity so that the rule is tested: the
    /// world ready, no start-up preparation pending, and -- in XR -- the head tracked. Until all three hold at once, in
    /// one frame, no frame is counted, a saved agreement or not: each is asked as it stands in that frame (a head
    /// tracked a moment ago and lost since is not tracked; a preparation that began again is pending). From the frame
    /// they first hold together play has begun for this world and none of them is asked again (a long frame of ordinary
    /// play is play and stays in the record). The frame since which each has held without a break is kept, for the one
    /// line the log gets.
    /// </summary>
    public sealed class PlayStartConditions
    {
        public bool Began { get; private set; }
        public int FirstAskedFrame { get; private set; } = -1;
        public int WorldReadyFrame { get; private set; } = -1;
        public int PreparedFrame { get; private set; } = -1;
        public int TrackedFrame { get; private set; } = -1;
        public int BeganFrame { get; private set; } = -1;
        public double FirstAskedSeconds { get; private set; }
        public double BeganSeconds { get; private set; }
        public int MostPending { get; private set; }

        /// <summary>A new world (another scene): play has not begun for it.</summary>
        public void Reset()
        {
            Began = false;
            FirstAskedFrame = WorldReadyFrame = PreparedFrame = TrackedFrame = BeganFrame = -1;
            FirstAskedSeconds = BeganSeconds = 0.0;
            MostPending = 0;
        }

        /// <summary>
        /// One frame's look. True from the frame play begins. <paramref name="xrActive"/> false: no head to wait for.
        /// Returns whether play begins in this very frame (once), by <paramref name="beganNow"/>.
        /// </summary>
        public bool Ask(bool worldReady, int pendingPreparations, bool xrActive, bool headTracked, int frame, double seconds, out bool beganNow)
        {
            beganNow = false;
            if (Began)
            {
                return true;
            }

            if (FirstAskedFrame < 0) { FirstAskedFrame = frame; FirstAskedSeconds = seconds; }
            if (pendingPreparations > MostPending) MostPending = pendingPreparations;
            bool prepared = pendingPreparations <= 0;
            bool tracked = !xrActive || headTracked;
            // Each condition's frame is the one since which it has held without a break: lost again, it starts over.
            WorldReadyFrame = worldReady ? (WorldReadyFrame < 0 ? frame : WorldReadyFrame) : -1;
            PreparedFrame = worldReady && prepared ? (PreparedFrame < 0 ? frame : PreparedFrame) : -1;
            TrackedFrame = tracked ? (TrackedFrame < 0 ? frame : TrackedFrame) : -1;
            if (!(worldReady && prepared && tracked))
            {
                return false;
            }

            Began = true;
            BeganFrame = frame;
            BeganSeconds = seconds;
            beganNow = true;
            return true;
        }

        /// <summary>One line for the log: where play began and what each condition's first frame was.</summary>
        public string Describe()
        {
            if (!Began) return "play has not begun (world ready at frame " + WorldReadyFrame + ", prepared at frame " + PreparedFrame + ", head tracked at frame " + TrackedFrame + ")";
            return "play began at frame " + BeganFrame + ", " + (BeganSeconds - FirstAskedSeconds).ToString("0.00") + " s after frame " + FirstAskedFrame
                + " (world ready at frame " + WorldReadyFrame + ", preparations ended at frame " + PreparedFrame + " -- the most pending " + MostPending
                + ", head tracked at frame " + TrackedFrame + "); no frame before it is counted";
        }
    }

    /// <summary>Which focus tells whether the player can play (DESIGN 21.18).</summary>
    public static class PlayFocus
    {
        /// <summary>
        /// In XR by an OpenXR session, the session's own focus: the headset is where the play is, and the desktop
        /// window may be out of focus while it goes on (a click on another window), or in focus while the system menu
        /// covers the game. Without XR (or with a loader whose session state is not known) the desktop window's focus.
        /// </summary>
        public static bool ForPlay(bool xrActive, bool openXr, bool xrSessionFocused, bool desktopFocused)
        {
            return xrActive && openXr ? xrSessionFocused : desktopFocused;
        }

        /// <summary>
        /// Whether the game is to be paused for the XR focus (DESIGN 4.6, D-241): in XR by an OpenXR session, the
        /// session's own input focus lost (the system UI over the game) pauses it; XR gone inactive after it had been
        /// active pauses it too (a runtime restarting), rather than letting the desktop window's focus resume the
        /// game. Never before XR was first active (the start-up, a PC without XR), and never by the desktop window's
        /// focus: without XR the game is not paused by focus at all, and in XR the window's focus is not the headset's.
        /// </summary>
        public static bool PauseByFocus(bool xrActive, bool xrEverActive, bool openXr, bool xrSessionFocused)
        {
            if (!xrEverActive) return false;
            if (!xrActive) return true;
            return openXr && !xrSessionFocused;
        }
    }
}
