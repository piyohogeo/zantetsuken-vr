using System;

namespace Zantetsu.Core
{
    /// <summary>
    /// The OpenXR session's state as the runtime last told it (DESIGN 4.6, the view-height diagnosis): written by the
    /// OpenXR feature that receives the session events (Zantetsu.Quest), read by anything that records a frame. The
    /// package's own log lines carry the state with a clock time but no frame; this keeps the frame too. Unknown (0)
    /// until a session event came in this session.
    /// </summary>
    public static class XrSessionStateReport
    {
        private static readonly string[] Names =
        {
            "UNKNOWN", "IDLE", "READY", "SYNCHRONIZED", "VISIBLE", "FOCUSED", "STOPPING", "LOSS_PENDING", "EXITING",
        };

        /// <summary>The state's number as OpenXR gives it (XrSessionState): 0 unknown, 1 idle, 2 ready, 3 synchronized, 4 visible, 5 focused, 6 stopping, 7 loss pending, 8 exiting.</summary>
        public static int State { get; private set; }

        /// <summary>The frame the last change was told in (-1: none yet).</summary>
        public static int ChangedFrame { get; private set; } = -1;

        /// <summary>How many changes were told in this session.</summary>
        public static int Changes { get; private set; }

        /// <summary>Told after each change: the old state, the new state.</summary>
        public static event Action<int, int> Changed;

        public static string Name => NameOf(State);

        public static string NameOf(int state) => state >= 0 && state < Names.Length ? Names[state] : "STATE_" + state;

        /// <summary>The feature's report of one change (from OpenXRFeature.OnSessionStateChange).</summary>
        public static void Set(int oldState, int newState, int frame)
        {
            State = newState;
            ChangedFrame = frame;
            Changes++;
            Changed?.Invoke(oldState, newState);
        }

        /// <summary>For tests and a new session: forgets every state told.</summary>
        public static void Reset()
        {
            State = 0;
            ChangedFrame = -1;
            Changes = 0;
            Changed = null;
        }
    }
}
