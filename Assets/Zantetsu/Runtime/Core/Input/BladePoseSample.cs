using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// A tracking sample produced by an input source. Reference-free value
    /// type; the caller supplies the frame ID, timestamp, and pose. This type
    /// never fetches time or frame information itself, and holds no velocity,
    /// angular velocity, or slash state.
    /// </summary>
    public readonly struct BladePoseSample
    {
        public readonly long FrameId;
        public readonly double TimestampSeconds;
        public readonly Vector3 GripPosition;
        public readonly Quaternion GripRotation;
        public readonly BladeTrackingState TrackingState;

        public BladePoseSample(long frameId, double timestampSeconds, Vector3 gripPosition, Quaternion gripRotation, BladeTrackingState trackingState)
        {
            FrameId = frameId;
            TimestampSeconds = timestampSeconds;
            GripPosition = gripPosition;
            GripRotation = gripRotation;
            TrackingState = trackingState;
        }

        public bool IsPositionTracked => (TrackingState & BladeTrackingState.Position) != 0;

        public bool IsRotationTracked => (TrackingState & BladeTrackingState.Rotation) != 0;

        public bool IsFullyTracked => IsPositionTracked && IsRotationTracked;
    }
}
