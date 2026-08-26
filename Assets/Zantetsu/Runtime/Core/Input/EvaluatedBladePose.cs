using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// World-space blade information after applying the grip-to-katana offset.
    /// Reference-free value type.
    /// </summary>
    public readonly struct EvaluatedBladePose
    {
        public readonly long FrameId;
        public readonly double TimestampSeconds;
        public readonly Pose KatanaPose;
        public readonly Vector3 BladeAxis;
        public readonly Vector3 EdgeDirection;
        public readonly Vector3 SideNormal;
        public readonly Vector3 CutSamplePosition;

        public EvaluatedBladePose(
            long frameId,
            double timestampSeconds,
            Pose katanaPose,
            Vector3 bladeAxis,
            Vector3 edgeDirection,
            Vector3 sideNormal,
            Vector3 cutSamplePosition)
        {
            FrameId = frameId;
            TimestampSeconds = timestampSeconds;
            KatanaPose = katanaPose;
            BladeAxis = bladeAxis;
            EdgeDirection = edgeDirection;
            SideNormal = sideNormal;
            CutSamplePosition = cutSamplePosition;
        }
    }
}
