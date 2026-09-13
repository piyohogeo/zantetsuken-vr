using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Schema v1 Capture Run Ready Marker: the value-only contract of
    /// a durable <c>run.ready</c> file. It binds a TestRunId and the 128-bit
    /// initialization ID to the content hashes of the two <c>run.init</c>
    /// markers. No public constructor is provided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SchemaVersion"/> is fixed at 1. Values are held only after
    /// every validation succeeds. This type owns, disposes, generates, and
    /// registers nothing and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunReadyMarker
    {
        private readonly long _testRunId;
        private readonly string _runInitializationId;
        private readonly string _initSha256;

        internal CaptureRunReadyMarker(
            long testRunId,
            string runInitializationId,
            string initSha256)
        {
            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (runInitializationId == null)
            {
                throw new ArgumentNullException(nameof(runInitializationId));
            }

            if (!IsLowercaseHex(runInitializationId, 32))
            {
                throw new ArgumentException("Run initialization ID must be 32 lowercase ASCII hex characters.", nameof(runInitializationId));
            }

            if (initSha256 == null)
            {
                throw new ArgumentNullException(nameof(initSha256));
            }

            if (!IsLowercaseHex(initSha256, 64))
            {
                throw new ArgumentException("Init SHA-256 must be 64 lowercase ASCII hex characters.", nameof(initSha256));
            }

            _testRunId = testRunId;
            _runInitializationId = runInitializationId;
            _initSha256 = initSha256;
        }

        internal int SchemaVersion => 1;

        internal long TestRunId => _testRunId;

        internal string RunInitializationId => _runInitializationId;

        internal string InitSha256 => _initSha256;

        private static bool IsLowercaseHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
