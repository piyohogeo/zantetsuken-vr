using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Numeric evaluation of blade motion between two evaluated blade poses.
    /// Reference-free value type. Units: position = m, time = s, speed = m/s.
    /// </summary>
    public readonly struct BladeMotionSample
    {
        public readonly long FromFrameId;
        public readonly long ToFrameId;

        public readonly double FromTimestampSeconds;
        public readonly double ToTimestampSeconds;
        public readonly double DeltaTimeSeconds;

        public readonly Vector3 CutSampleDisplacement;
        public readonly Vector3 CutSampleVelocity;
        public readonly Vector3 LateralVelocity;

        public readonly float Speed;
        public readonly float LateralSpeed;
        public readonly float EdgeLeadScore;
        public readonly bool HasLateralMotion;

        public BladeMotionSample(
            long fromFrameId,
            long toFrameId,
            double fromTimestampSeconds,
            double toTimestampSeconds,
            double deltaTimeSeconds,
            Vector3 cutSampleDisplacement,
            Vector3 cutSampleVelocity,
            Vector3 lateralVelocity,
            float speed,
            float lateralSpeed,
            float edgeLeadScore,
            bool hasLateralMotion)
        {
            FromFrameId = fromFrameId;
            ToFrameId = toFrameId;
            FromTimestampSeconds = fromTimestampSeconds;
            ToTimestampSeconds = toTimestampSeconds;
            DeltaTimeSeconds = deltaTimeSeconds;
            CutSampleDisplacement = cutSampleDisplacement;
            CutSampleVelocity = cutSampleVelocity;
            LateralVelocity = lateralVelocity;
            Speed = speed;
            LateralSpeed = lateralSpeed;
            EdgeLeadScore = edgeLeadScore;
            HasLateralMotion = hasLateralMotion;
        }
    }
}
