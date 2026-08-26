using System;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Independent tracking availability flags for a blade pose sample. A
    /// missing component is never auto-filled into a valid pose.
    /// </summary>
    [Flags]
    public enum BladeTrackingState
    {
        None = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
    }
}
