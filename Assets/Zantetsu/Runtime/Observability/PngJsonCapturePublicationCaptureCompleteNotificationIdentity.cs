using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable notification identity handed to an external sink: exactly the
    /// four values that identify one capture-complete notification — test run
    /// id, run initialization id, run manifest content SHA-256, and capture
    /// index path — compared ordinally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only fields and is produced only from a
    /// notification operation. It duplicates no status, disposition, root
    /// layout, lease, token, timestamp, random value, or generated id, and
    /// holds no reference to the operation or any cleanup result. It touches no
    /// filesystem.
    /// </para>
    /// <para>
    /// It provides only the two comparisons a sink needs: same-identity
    /// equality and the same-test-run-id conflict predicate.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotificationIdentity
    {
        private readonly long _testRunId;
        private readonly string _runInitializationId;
        private readonly string _runManifestContentSha256;
        private readonly string _captureIndexPath;

        private PngJsonCapturePublicationCaptureCompleteNotificationIdentity(
            long testRunId,
            string runInitializationId,
            string runManifestContentSha256,
            string captureIndexPath)
        {
            _testRunId = testRunId;
            _runInitializationId = runInitializationId;
            _runManifestContentSha256 = runManifestContentSha256;
            _captureIndexPath = captureIndexPath;
        }

        /// <summary>
        /// Single construction boundary: builds the identity from the
        /// operation's four stable values exactly once. A null operation is
        /// rejected with <see cref="ArgumentNullException"/>.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteNotificationIdentity From(
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return new PngJsonCapturePublicationCaptureCompleteNotificationIdentity(
                operation.TestRunId,
                operation.RunInitializationId,
                operation.RunManifestContentSha256,
                operation.CaptureIndexPath);
        }

        internal long TestRunId => _testRunId;

        internal string RunInitializationId => _runInitializationId;

        internal string RunManifestContentSha256 => _runManifestContentSha256;

        internal string CaptureIndexPath => _captureIndexPath;

        /// <summary>
        /// Exact same-identity predicate: all four values must match, with the
        /// three strings compared ordinally. Never throws.
        /// </summary>
        internal bool IsSameIdentity(PngJsonCapturePublicationCaptureCompleteNotificationIdentity other)
        {
            return other != null
                && _testRunId == other._testRunId
                && string.Equals(_runInitializationId, other._runInitializationId, StringComparison.Ordinal)
                && string.Equals(_runManifestContentSha256, other._runManifestContentSha256, StringComparison.Ordinal)
                && string.Equals(_captureIndexPath, other._captureIndexPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// Conflict predicate: the same test run id with any of the other three
        /// values differing. Never throws.
        /// </summary>
        internal bool ConflictsWith(PngJsonCapturePublicationCaptureCompleteNotificationIdentity other)
        {
            return other != null
                && _testRunId == other._testRunId
                && !IsSameIdentity(other);
        }
    }
}
