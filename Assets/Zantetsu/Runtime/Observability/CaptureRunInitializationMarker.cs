using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Schema v1 Capture Run Initialization Marker: the value-only
    /// contract of a durable <c>run.init</c> file. It binds a TestRunId to the
    /// 128-bit initialization ID, the owning root role, and the two derived Run
    /// root hashes without generating, correcting, or normalizing any of them.
    /// No public constructor is provided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SchemaVersion"/> is fixed at 1. Values are held only after
    /// every validation succeeds. This type owns, disposes, generates, and
    /// registers nothing and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationMarker
    {
        private readonly long _testRunId;
        private readonly string _runInitializationId;
        private readonly CaptureRunRootRole _rootRole;
        private readonly string _stagingRunRootSha256;
        private readonly string _finalRunRootSha256;

        internal CaptureRunInitializationMarker(
            long testRunId,
            string runInitializationId,
            CaptureRunRootRole rootRole,
            string stagingRunRootSha256,
            string finalRunRootSha256)
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

            if (rootRole != CaptureRunRootRole.Staging && rootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentOutOfRangeException(nameof(rootRole), rootRole, "Root role must be Staging or Final.");
            }

            if (stagingRunRootSha256 == null)
            {
                throw new ArgumentNullException(nameof(stagingRunRootSha256));
            }

            if (!IsLowercaseHex(stagingRunRootSha256, 64))
            {
                throw new ArgumentException("Staging run root SHA-256 must be 64 lowercase ASCII hex characters.", nameof(stagingRunRootSha256));
            }

            if (finalRunRootSha256 == null)
            {
                throw new ArgumentNullException(nameof(finalRunRootSha256));
            }

            if (!IsLowercaseHex(finalRunRootSha256, 64))
            {
                throw new ArgumentException("Final run root SHA-256 must be 64 lowercase ASCII hex characters.", nameof(finalRunRootSha256));
            }

            _testRunId = testRunId;
            _runInitializationId = runInitializationId;
            _rootRole = rootRole;
            _stagingRunRootSha256 = stagingRunRootSha256;
            _finalRunRootSha256 = finalRunRootSha256;
        }

        internal int SchemaVersion => 1;

        internal long TestRunId => _testRunId;

        internal string RunInitializationId => _runInitializationId;

        internal CaptureRunRootRole RootRole => _rootRole;

        internal string StagingRunRootSha256 => _stagingRunRootSha256;

        internal string FinalRunRootSha256 => _finalRunRootSha256;

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
