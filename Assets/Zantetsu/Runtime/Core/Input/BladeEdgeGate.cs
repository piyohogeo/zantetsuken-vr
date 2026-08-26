using System;
using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Stateless edge direction gate. Evaluates a single
    /// <see cref="BladeMotionSample"/> against a
    /// <see cref="BladeEdgeGateSettings"/> contract. Never throws, logs,
    /// allocates, or mutates its inputs on the hot path.
    /// </summary>
    public static class BladeEdgeGate
    {
        /// <summary>
        /// Evaluates the sample. The first failing check wins, in this order:
        /// <list type="number">
        /// <item>Input validity (<see cref="BladeEdgeGateReason.InvalidInput"/>)</item>
        /// <item>Window lower bound (<see cref="BladeEdgeGateReason.WindowTooShort"/>)</item>
        /// <item>Window upper bound (<see cref="BladeEdgeGateReason.WindowTooLong"/>)</item>
        /// <item>Minimum speed (<see cref="BladeEdgeGateReason.SpeedBelowMinimum"/>)</item>
        /// <item>Minimum displacement (<see cref="BladeEdgeGateReason.DisplacementBelowMinimum"/>)</item>
        /// <item>Lateral motion presence (<see cref="BladeEdgeGateReason.NoLateralMotion"/>)</item>
        /// <item>Edge lead score (<see cref="BladeEdgeGateReason.EdgeLeadBelowThreshold"/>)</item>
        /// </list>
        /// </summary>
        public static BladeEdgeGateDecision Evaluate(in BladeMotionSample sample, in BladeEdgeGateSettings settings)
        {
            if (!IsValidInput(sample))
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.InvalidInput);
            }

            if (sample.DeltaTimeSeconds < settings.MinimumWindowSeconds)
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.WindowTooShort);
            }

            if (sample.DeltaTimeSeconds > settings.MaximumWindowSeconds)
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.WindowTooLong);
            }

            if (sample.Speed < settings.MinimumSpeed)
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.SpeedBelowMinimum);
            }

            if (DisplacementLength(sample.CutSampleDisplacement) < settings.MinimumDisplacement)
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.DisplacementBelowMinimum);
            }

            if (!sample.HasLateralMotion)
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.NoLateralMotion);
            }

            if (!(sample.EdgeLeadScore > settings.MinimumEdgeLeadScore))
            {
                return new BladeEdgeGateDecision(BladeEdgeGateReason.EdgeLeadBelowThreshold);
            }

            return new BladeEdgeGateDecision(BladeEdgeGateReason.None);
        }

        private static bool IsValidInput(in BladeMotionSample sample)
        {
            if (double.IsNaN(sample.FromTimestampSeconds) || double.IsInfinity(sample.FromTimestampSeconds)
                || double.IsNaN(sample.ToTimestampSeconds) || double.IsInfinity(sample.ToTimestampSeconds)
                || double.IsNaN(sample.DeltaTimeSeconds) || double.IsInfinity(sample.DeltaTimeSeconds))
            {
                return false;
            }

            if (!(sample.DeltaTimeSeconds > 0.0) || !(sample.ToTimestampSeconds > sample.FromTimestampSeconds))
            {
                return false;
            }

            if (!IsFinite(sample.CutSampleDisplacement) || !IsFinite(sample.CutSampleVelocity) || !IsFinite(sample.LateralVelocity))
            {
                return false;
            }

            if (!float.IsFinite(sample.Speed) || sample.Speed < 0f)
            {
                return false;
            }

            if (!float.IsFinite(sample.LateralSpeed) || sample.LateralSpeed < 0f)
            {
                return false;
            }

            if (!float.IsFinite(sample.EdgeLeadScore) || sample.EdgeLeadScore < -1f || sample.EdgeLeadScore > 1f)
            {
                return false;
            }

            return true;
        }

        // World-space length computed in double to avoid float squared-sum overflow.
        private static double DisplacementLength(Vector3 displacement)
        {
            double x = displacement.x;
            double y = displacement.y;
            double z = displacement.z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
    }
}
