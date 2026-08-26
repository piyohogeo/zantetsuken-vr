using System;
using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Stateless numeric evaluation of blade motion between two evaluated blade
    /// poses. Pure functions; never throws or logs on the hot path.
    /// </summary>
    public static class BladeMotionEvaluator
    {
        private const float MinLateralSpeedSquared = 1e-12f;

        /// <summary>
        /// Evaluates the cut sample point velocity, lateral velocity, and edge
        /// lead score between two evaluated blade poses. Returns false (and sets
        /// <paramref name="result"/> to default) for invalid input or numeric
        /// failure.
        /// </summary>
        public static bool TryEvaluate(
            in EvaluatedBladePose previous,
            in EvaluatedBladePose current,
            out BladeMotionSample result)
        {
            result = default;

            if (double.IsNaN(previous.TimestampSeconds) || double.IsInfinity(previous.TimestampSeconds)
                || double.IsNaN(current.TimestampSeconds) || double.IsInfinity(current.TimestampSeconds))
            {
                return false;
            }

            if (!(current.TimestampSeconds > previous.TimestampSeconds))
            {
                return false;
            }

            double deltaTime = current.TimestampSeconds - previous.TimestampSeconds;
            if (double.IsNaN(deltaTime) || double.IsInfinity(deltaTime) || !(deltaTime > 0.0))
            {
                return false;
            }

            double inverseDeltaTime = 1.0 / deltaTime;
            if (double.IsNaN(inverseDeltaTime) || double.IsInfinity(inverseDeltaTime))
            {
                return false;
            }

            if (!BladePoseValidation.IsFinite(previous.CutSamplePosition) || !BladePoseValidation.IsFinite(current.CutSamplePosition))
            {
                return false;
            }

            Vector3 bladeAxis = current.BladeAxis;
            Vector3 edgeDirection = current.EdgeDirection;

            if (!BladePoseValidation.HasValidBladeAxes(current))
            {
                return false;
            }

            Vector3 displacement = current.CutSamplePosition - previous.CutSamplePosition;
            if (!BladePoseValidation.IsFinite(displacement))
            {
                return false;
            }

            if (!TryComputeVelocity(displacement, inverseDeltaTime, out Vector3 velocity))
            {
                return false;
            }

            float speedSq = velocity.sqrMagnitude;
            if (!float.IsFinite(speedSq))
            {
                return false;
            }

            float speed = Mathf.Sqrt(speedSq);

            Vector3 lateralVelocity = velocity - Vector3.Dot(velocity, bladeAxis) * bladeAxis;
            if (!BladePoseValidation.IsFinite(lateralVelocity))
            {
                return false;
            }

            float lateralSpeedSq = lateralVelocity.sqrMagnitude;
            if (!float.IsFinite(lateralSpeedSq))
            {
                return false;
            }

            float lateralSpeed = Mathf.Sqrt(lateralSpeedSq);

            bool hasLateralMotion = lateralSpeedSq > MinLateralSpeedSquared;
            float edgeLeadScore = 0f;
            if (hasLateralMotion)
            {
                Vector3 lateralDirection = Normalize(lateralVelocity);
                edgeLeadScore = Mathf.Clamp(Vector3.Dot(lateralDirection, edgeDirection), -1f, 1f);

                if (!float.IsFinite(edgeLeadScore))
                {
                    result = default;
                    return false;
                }
            }

            result = new BladeMotionSample(
                previous.FrameId,
                current.FrameId,
                previous.TimestampSeconds,
                current.TimestampSeconds,
                deltaTime,
                displacement,
                velocity,
                lateralVelocity,
                speed,
                lateralSpeed,
                edgeLeadScore,
                hasLateralMotion);

            return true;
        }

        private static bool TryComputeVelocity(Vector3 displacement, double inverseDeltaTime, out Vector3 velocity)
        {
            double vx = (double)displacement.x * inverseDeltaTime;
            double vy = (double)displacement.y * inverseDeltaTime;
            double vz = (double)displacement.z * inverseDeltaTime;

            if (double.IsNaN(vx) || double.IsInfinity(vx)
                || double.IsNaN(vy) || double.IsInfinity(vy)
                || double.IsNaN(vz) || double.IsInfinity(vz))
            {
                velocity = default;
                return false;
            }

            double max = (double)float.MaxValue;
            if (Math.Abs(vx) > max || Math.Abs(vy) > max || Math.Abs(vz) > max)
            {
                velocity = default;
                return false;
            }

            velocity = new Vector3((float)vx, (float)vy, (float)vz);
            return true;
        }

        // Explicit normalization that does not rely on Vector3.normalized's
        // implicit zeroing of very short vectors.
        private static Vector3 Normalize(Vector3 v)
        {
            float length = Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            return new Vector3(v.x / length, v.y / length, v.z / length);
        }
    }
}
