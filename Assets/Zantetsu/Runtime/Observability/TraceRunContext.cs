using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable description of a single run's environment and reproducibility
    /// conditions. All values are supplied by the caller from a single capture
    /// point; this type performs no Unity environment lookup.
    /// </summary>
    public sealed class TraceRunContext
    {
        public TraceRunContext(
            long testRunId,
            long capturedUtcUnixMilliseconds,
            string buildId,
            string unityVersion,
            string packageLockSha256,
            string sceneId,
            long randomSeed,
            double fixedDeltaTimeSeconds,
            int qualityLevel,
            string qualityName,
            int worldPhysicsProfileVersion,
            Vector3 gravity)
        {
            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (capturedUtcUnixMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capturedUtcUnixMilliseconds), capturedUtcUnixMilliseconds, "Captured UTC Unix milliseconds must not be negative.");
            }

            BuildId = ValidateRequiredString(buildId, nameof(buildId));
            UnityVersion = ValidateRequiredString(unityVersion, nameof(unityVersion));
            PackageLockSha256 = ValidateSha256(packageLockSha256, nameof(packageLockSha256));
            SceneId = ValidateRequiredString(sceneId, nameof(sceneId));

            if (double.IsNaN(fixedDeltaTimeSeconds) || double.IsInfinity(fixedDeltaTimeSeconds) || fixedDeltaTimeSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fixedDeltaTimeSeconds), fixedDeltaTimeSeconds, "Fixed delta time must be finite and greater than zero.");
            }

            if (qualityLevel < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(qualityLevel), qualityLevel, "Quality level must not be negative.");
            }

            QualityName = ValidateRequiredString(qualityName, nameof(qualityName));

            if (worldPhysicsProfileVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(worldPhysicsProfileVersion), worldPhysicsProfileVersion, "World physics profile version must be greater than zero.");
            }

            if (!IsFinite(gravity.x) || !IsFinite(gravity.y) || !IsFinite(gravity.z))
            {
                throw new ArgumentOutOfRangeException(nameof(gravity), gravity, "Gravity components must all be finite.");
            }

            TestRunId = testRunId;
            CapturedUtcUnixMilliseconds = capturedUtcUnixMilliseconds;
            RandomSeed = randomSeed;
            FixedDeltaTimeSeconds = fixedDeltaTimeSeconds;
            QualityLevel = qualityLevel;
            WorldPhysicsProfileVersion = worldPhysicsProfileVersion;
            Gravity = gravity;
        }

        public long TestRunId { get; }

        public long CapturedUtcUnixMilliseconds { get; }

        public string BuildId { get; }

        public string UnityVersion { get; }

        public string PackageLockSha256 { get; }

        public string SceneId { get; }

        public long RandomSeed { get; }

        public double FixedDeltaTimeSeconds { get; }

        public int QualityLevel { get; }

        public string QualityName { get; }

        public int WorldPhysicsProfileVersion { get; }

        public Vector3 Gravity { get; }

        private static string ValidateRequiredString(string value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must not be null, empty, or whitespace.", paramName);
            }

            return value;
        }

        private static string ValidateSha256(string value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (value.Length != 64)
            {
                throw new ArgumentException("Value must be exactly 64 hexadecimal characters.", paramName);
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    throw new ArgumentException("Value must contain only hexadecimal characters.", paramName);
                }
            }

            return value.ToLowerInvariant();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
