using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Stateless conversion from a grip pose sample to world-space blade
    /// information. Pure functions; never throws or logs on the hot path.
    /// </summary>
    public static class BladePoseAdapter
    {
        private const float MinQuaternionLengthSquared = 1e-12f;

        /// <summary>
        /// Evaluates a blade pose sample into world-space blade information.
        /// Returns false (and sets <paramref name="result"/> to default) when
        /// tracking is incomplete or any input is invalid.
        /// </summary>
        public static bool TryEvaluate(
            in BladePoseSample sample,
            in Pose gripToKatanaOffset,
            in BladeFrame bladeFrame,
            out EvaluatedBladePose result)
        {
            result = default;

            if (!sample.IsFullyTracked)
            {
                return false;
            }

            if (double.IsNaN(sample.TimestampSeconds) || double.IsInfinity(sample.TimestampSeconds))
            {
                return false;
            }

            if (!IsFinite(sample.GripPosition) || !IsFinite(sample.GripRotation))
            {
                return false;
            }

            if (!IsFinite(gripToKatanaOffset.position) || !IsFinite(gripToKatanaOffset.rotation))
            {
                return false;
            }

            if (!bladeFrame.IsValid)
            {
                return false;
            }

            float gripRotationLengthSq = Quaternion.Dot(sample.GripRotation, sample.GripRotation);
            if (!float.IsFinite(gripRotationLengthSq) || gripRotationLengthSq <= MinQuaternionLengthSquared)
            {
                return false;
            }

            float offsetRotationLengthSq = Quaternion.Dot(gripToKatanaOffset.rotation, gripToKatanaOffset.rotation);
            if (!float.IsFinite(offsetRotationLengthSq) || offsetRotationLengthSq <= MinQuaternionLengthSquared)
            {
                return false;
            }

            Quaternion gripRotation = Normalize(sample.GripRotation);
            Quaternion offsetRotation = Normalize(gripToKatanaOffset.rotation);

            Quaternion katanaRotation = Normalize(gripRotation * offsetRotation);
            if (!IsFinite(katanaRotation))
            {
                result = default;
                return false;
            }

            Vector3 katanaPosition = sample.GripPosition + gripRotation * gripToKatanaOffset.position;

            Vector3 bladeAxis = katanaRotation * bladeFrame.BladeAxis;
            Vector3 edgeDirection = katanaRotation * bladeFrame.EdgeDirection;
            Vector3 sideNormal = katanaRotation * bladeFrame.SideNormal;
            Vector3 cutSamplePosition = katanaPosition + katanaRotation * bladeFrame.CutSamplePoint;

            if (!IsFinite(katanaPosition) || !IsFinite(bladeAxis) || !IsFinite(edgeDirection) || !IsFinite(sideNormal) || !IsFinite(cutSamplePosition))
            {
                result = default;
                return false;
            }

            result = new EvaluatedBladePose(
                sample.FrameId,
                sample.TimestampSeconds,
                new Pose(katanaPosition, katanaRotation),
                bladeAxis,
                edgeDirection,
                sideNormal,
                cutSamplePosition);

            return true;
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float length = Mathf.Sqrt(Quaternion.Dot(q, q));
            return new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private static bool IsFinite(Quaternion q)
        {
            return !float.IsNaN(q.x) && !float.IsInfinity(q.x)
                && !float.IsNaN(q.y) && !float.IsInfinity(q.y)
                && !float.IsNaN(q.z) && !float.IsInfinity(q.z)
                && !float.IsNaN(q.w) && !float.IsInfinity(q.w);
        }
    }
}
