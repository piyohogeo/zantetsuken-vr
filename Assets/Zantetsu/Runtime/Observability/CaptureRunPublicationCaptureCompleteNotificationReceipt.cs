using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one capture-complete notification: which
    /// notifier issued it and which notification operation it accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the issuing
    /// notifier and the notification operation — and has no public
    /// constructor. The constructor rejects a null issuer with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>issuedBy</c>, a null operation with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, and an invalid operation with
    /// <see cref="ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, storing the two references only after every check
    /// succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks without throwing. Every other accessor forwards a value from the
    /// held operation: the cleanup orchestration result, the cleanup execution
    /// result, root layout, lock lease, test run id, run initialization id, run
    /// manifest content SHA-256, capture index path, disposition, and status.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationReceipt
    {
        private readonly ICaptureRunPublicationCaptureCompleteNotifier _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteNotificationOperation _operation;

        internal CaptureRunPublicationCaptureCompleteNotificationReceipt(
            ICaptureRunPublicationCaptureCompleteNotifier issuedBy,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "Notification operation must be valid.",
                    nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunPublicationCaptureCompleteNotifier IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _operation.CleanupResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _operation.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockLease LockLease => _operation.LockLease;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _operation.Disposition;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _operation.Status;

        internal bool IsValid
        {
            get
            {
                return _issuedBy != null && _operation != null && _operation.IsValid;
            }
        }

        internal bool IsIssuedFor(
            ICaptureRunPublicationCaptureCompleteNotifier notifier,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation)
        {
            return notifier != null
                && operation != null
                && ReferenceEquals(_issuedBy, notifier)
                && ReferenceEquals(_operation, operation)
                && _operation.IsValid;
        }
    }
}
