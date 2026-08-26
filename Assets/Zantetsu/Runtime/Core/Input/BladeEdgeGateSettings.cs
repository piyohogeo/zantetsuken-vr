using System;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Tunable thresholds for the edge direction gate. Reference-free value
    /// type. The constructor rejects invalid values.
    /// </summary>
    public readonly struct BladeEdgeGateSettings
    {
        public readonly double MinimumWindowSeconds;
        public readonly double MaximumWindowSeconds;
        public readonly float MinimumSpeed;
        public readonly float MinimumDisplacement;
        public readonly float MinimumEdgeLeadScore;

        public BladeEdgeGateSettings(
            double minimumWindowSeconds,
            double maximumWindowSeconds,
            float minimumSpeed,
            float minimumDisplacement,
            float minimumEdgeLeadScore)
        {
            if (double.IsNaN(minimumWindowSeconds) || double.IsInfinity(minimumWindowSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(minimumWindowSeconds));
            }

            if (double.IsNaN(maximumWindowSeconds) || double.IsInfinity(maximumWindowSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumWindowSeconds));
            }

            if (!(minimumWindowSeconds > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(minimumWindowSeconds), "Minimum window must be greater than zero.");
            }

            if (!(maximumWindowSeconds >= minimumWindowSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumWindowSeconds), "Maximum window must be greater than or equal to the minimum window.");
            }

            if (!float.IsFinite(minimumSpeed) || minimumSpeed < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumSpeed));
            }

            if (!float.IsFinite(minimumDisplacement) || minimumDisplacement < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumDisplacement));
            }

            if (!float.IsFinite(minimumEdgeLeadScore) || minimumEdgeLeadScore < -1f || minimumEdgeLeadScore > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumEdgeLeadScore));
            }

            MinimumWindowSeconds = minimumWindowSeconds;
            MaximumWindowSeconds = maximumWindowSeconds;
            MinimumSpeed = minimumSpeed;
            MinimumDisplacement = minimumDisplacement;
            MinimumEdgeLeadScore = minimumEdgeLeadScore;
        }

        internal static bool IsValid(in BladeEdgeGateSettings settings)
        {
            if (double.IsNaN(settings.MinimumWindowSeconds) || double.IsInfinity(settings.MinimumWindowSeconds)
                || double.IsNaN(settings.MaximumWindowSeconds) || double.IsInfinity(settings.MaximumWindowSeconds))
            {
                return false;
            }

            if (!(settings.MinimumWindowSeconds > 0.0) || !(settings.MaximumWindowSeconds >= settings.MinimumWindowSeconds))
            {
                return false;
            }

            if (!float.IsFinite(settings.MinimumSpeed) || settings.MinimumSpeed < 0f)
            {
                return false;
            }

            if (!float.IsFinite(settings.MinimumDisplacement) || settings.MinimumDisplacement < 0f)
            {
                return false;
            }

            if (!float.IsFinite(settings.MinimumEdgeLeadScore) || settings.MinimumEdgeLeadScore < -1f || settings.MinimumEdgeLeadScore > 1f)
            {
                return false;
            }

            return true;
        }
    }
}
