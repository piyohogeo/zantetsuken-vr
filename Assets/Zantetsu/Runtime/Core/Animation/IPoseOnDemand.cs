using UnityEngine;

namespace Zantetsu.Core.Animation
{
    /// <summary>
    /// A character whose bones may stand behind the frame -- a level of detail updates them now and then, or only some
    /// of them -- as a hit reads it: where it can be at all, and its whole current pose put on the bones on demand.
    /// <para>
    /// **Range.** <see cref="RangeBounds"/>, in <see cref="Root"/>'s frame, holds every place the character's hit shape
    /// can be at any time of what it plays, made once at its preparation; with the root as it stands now, it says where
    /// a sweep could meet the character whatever the bones show, so a character is never passed over for bones left behind.
    /// </para>
    /// <para>
    /// **The current pose, once per frame.** <see cref="EnsureCurrentFullPose"/> applies this frame's whole pose to every
    /// bone, at this frame's time, unless that has been done in this frame already: several sweeps of one update, or
    /// several Slashes, share it. What reads the bones after it -- the exact hit test, the cut -- reads the current pose.
    /// </para>
    /// </summary>
    public interface IPoseOnDemand
    {
        /// <summary>The frame <see cref="RangeBounds"/> is in: the character's root as it stands.</summary>
        Transform Root { get; }

        /// <summary>Bounds in <see cref="Root"/>'s frame holding the hit shape at every time of what the character plays.</summary>
        Bounds RangeBounds { get; }

        /// <summary>Whether the character still plays a pose (not withdrawn, not stopped).</summary>
        bool IsLive { get; }

        /// <summary>
        /// Puts this frame's whole pose on the bones, at this frame's time, if it is not there yet in this frame. True
        /// when this call applied it; false when it was already applied in this frame or cannot be.
        /// </summary>
        bool EnsureCurrentFullPose();
    }
}
