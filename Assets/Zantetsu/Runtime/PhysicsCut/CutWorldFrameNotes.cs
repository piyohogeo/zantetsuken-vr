using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Heavy work of the current frame, noted by whoever did it, for whoever has to decide whether the frame can bear
    /// more (the compaction of the display's instance regions, DESIGN 5.6, D-202). One frame number a kind: a note is
    /// "this kind happened in this frame", nothing else -- no cost, no count, no queue -- and nothing polls or clears
    /// it; a note is stale the moment the frame number moves on. The main thread alone writes and reads it.
    /// </summary>
    public static class CutWorldFrameNotes
    {
        public enum Work
        {
            /// <summary>An NPC was started by the crowd's refill (its slot taken, its actor added and shown).</summary>
            NpcRefill,

            /// <summary>A returned NPC slot was prepared again, or its mesh baked, by the refill.</summary>
            NpcReprepare,

            /// <summary>A MobPlan cycle was published and taken (plans replaced, starts requested).</summary>
            MobPlanPublish,

            /// <summary>An NPC was taken in by the crowd (its actor added and activated: the display's start), at the beginning or by the refill.</summary>
            MobPlanIntake,

            /// <summary>Withdrawn NPCs were retired by the crowd (their slots returned).</summary>
            MobPlanReclaim,

            /// <summary>The static placed-target index was built again.</summary>
            StaticIndexRebuild,
        }

        private static readonly int[] s_frames = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private static readonly string[] s_names = { "NPC refill", "NPC re-preparation", "MobPlan publication", "MobPlan intake", "MobPlan reclaim", "static index rebuild" };

        // The frame of the note before the last, a kind: a frame's notes can then be read in the frame after it.
        private static readonly int[] s_framesBefore = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };

        /// <summary>This kind of work happened in the current frame.</summary>
        public static void Note(Work work)
        {
            int frame = Time.frameCount;
            if (s_frames[(int)work] != frame)
            {
                s_framesBefore[(int)work] = s_frames[(int)work];
                s_frames[(int)work] = frame;
            }
        }

        /// <summary>
        /// The kinds noted in <paramref name="frame"/>, a bit a kind (bit i = the i-th of <see cref="Work"/>): for the
        /// current frame or the one before it. An older frame's notes may be gone, and read as none.
        /// </summary>
        public static int MaskOf(int frame)
        {
            int mask = 0;
            for (int i = 0; i < s_frames.Length; i++)
            {
                if (s_frames[i] == frame || s_framesBefore[i] == frame) mask |= 1 << i;
            }

            return mask;
        }

        /// <summary>Whether any kind of work was noted in the current frame, and which (the first found).</summary>
        public static bool TryGetThisFrame(out string which)
        {
            int frame = Time.frameCount;
            for (int i = 0; i < s_frames.Length; i++)
            {
                if (s_frames[i] == frame)
                {
                    which = s_names[i];
                    return true;
                }
            }

            which = null;
            return false;
        }

        /// <summary>Tests only: every note forgotten.</summary>
        internal static void ClearForTest()
        {
            for (int i = 0; i < s_frames.Length; i++)
            {
                s_frames[i] = int.MinValue;
                s_framesBefore[i] = int.MinValue;
            }
        }
    }
}
