using System.Collections.Generic;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// When the view-height diagnosis records a frame (DESIGN 4.6, unit 6-A): the decision alone, with no engine in it.
    /// A transition (a trigger) opens a window of <see cref="WindowFrames"/> frames in which every frame is recorded; a
    /// trigger inside a window extends it to its own length from there. A jump of the head's local height between two
    /// frames whose head was tracked in both is a trigger of its own, so that a shift with no event before it is caught.
    /// Outside a window one frame in <see cref="SteadySeconds"/> is recorded. When a window ends, the mean height of its
    /// last <see cref="SettledFrames"/> recorded frames is the "settled" value of that transition. The last few frames
    /// before a trigger are kept so that the moment before the transition is in the record too.
    /// </summary>
    public sealed class HeadHeightTrace
    {
        public const int WindowFrames = 90;          // ~2 s at 45 Hz
        public const int SettledFrames = 30;
        public const float JumpMeters = 0.08f;
        public const double SteadySeconds = 10.0;
        public const int BeforeFrames = 3;

        private int _windowUntil = -1;
        private double _nextSteady;
        private bool _hasLast;
        private float _lastHeadY;
        private bool _lastTracked;
        private readonly List<float> _windowHeights = new List<float>();
        private readonly Queue<int> _before = new Queue<int>();   // the frames sampled but not recorded (their numbers), the last few

        /// <summary>Whether a window is open at this frame.</summary>
        public bool InWindow(int frame) => frame <= _windowUntil;

        /// <summary>
        /// A transition at this frame: opens or extends the window. Returns true when no window was open (the moment before
        /// it is worth recording), false when the window was only extended.
        /// </summary>
        public bool Trigger(int frame)
        {
            bool opened = frame > _windowUntil;
            if (opened)
            {
                _windowHeights.Clear();
            }

            _windowUntil = frame + WindowFrames;
            return opened;
        }

        /// <summary>
        /// One frame's head height, sampled every frame. Returns whether the frame is recorded, with why: "window",
        /// "jump" (a trigger of its own: the window opens or extends), or "steady". A jump is judged only between two
        /// frames with the head tracked in both: an untracked frame's value is no height.
        /// </summary>
        public bool Sample(int frame, double seconds, bool tracked, float headLocalY, out string why)
        {
            why = null;
            bool jump = tracked && _hasLast && _lastTracked && System.Math.Abs(headLocalY - _lastHeadY) > JumpMeters;
            _hasLast = true;
            _lastHeadY = headLocalY;
            _lastTracked = tracked;
            if (jump)
            {
                Trigger(frame);
                why = "jump";
            }
            else if (frame <= _windowUntil)
            {
                why = "window";
            }
            else if (seconds >= _nextSteady)
            {
                why = "steady";
            }

            if (why == null)
            {
                _before.Enqueue(frame);
                while (_before.Count > BeforeFrames) _before.Dequeue();
                return false;
            }

            if (why != "steady" && tracked)
            {
                _windowHeights.Add(headLocalY);
            }

            _nextSteady = seconds + SteadySeconds;   // a recorded frame of any kind is as good as a steady one
            return true;
        }

        /// <summary>
        /// At the first frame after a window: the mean of the tracked heights of its last recorded frames, and how many.
        /// False at every other frame, and at a window that recorded no tracked frame (count 0).
        /// </summary>
        public bool Settled(int frame, out float meanHeadLocalY, out int count)
        {
            meanHeadLocalY = 0f;
            count = 0;
            if (_windowUntil < 0 || frame != _windowUntil + 1)
            {
                return false;
            }

            _windowUntil = -1;
            int n = _windowHeights.Count;
            if (n == 0)
            {
                return true;
            }

            int from = n > SettledFrames ? n - SettledFrames : 0;
            double sum = 0;
            for (int i = from; i < n; i++) sum += _windowHeights[i];
            count = n - from;
            meanHeadLocalY = (float)(sum / count);
            _windowHeights.Clear();
            return true;
        }

        /// <summary>The numbers of the last frames sampled but not recorded (for the record of the moment before a trigger).</summary>
        public IEnumerable<int> BeforeFramesSampled => _before;
    }
}
