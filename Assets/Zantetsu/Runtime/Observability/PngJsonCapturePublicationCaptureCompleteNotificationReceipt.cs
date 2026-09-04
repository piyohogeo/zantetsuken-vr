using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one PngJson capture-complete notification:
    /// which notifier issued it and which notification operation it accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the issuing
    /// notifier and the notification operation — and has no public
    /// constructor. The atomic factory rejects a null issuer with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>issuedBy</c>, a null operation with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, and an invalid operation with
    /// <see cref="ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, storing the two references only after every check
    /// succeeds. The factory performs no notification side effect; it is the
    /// boundary a notifier calls after the notification was accepted.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks without throwing. Every other accessor forwards a value from the
    /// held operation: the cleanup orchestration result, the cleanup execution
    /// result, root layout, lock identity evidence, test run id, run
    /// initialization id, run manifest content SHA-256, capture index path,
    /// disposition, and status.
    /// </para>
    /// <para>
    /// The type owns, mutates, and disposes nothing — no token, lease, array,
    /// stream, or byte sequence, and no duplicate notification identity field —
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotificationReceipt
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteNotifier _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationOperation _operation;

        private PngJsonCapturePublicationCaptureCompleteNotificationReceipt(
            IPngJsonCapturePublicationCaptureCompleteNotifier issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
        {
            _issuedBy = issuedBy;
            _operation = operation;
        }

        /// <summary>
        /// Atomic issuance gate: null-checks every input, requires the
        /// operation to be currently valid, and only then binds the two exact
        /// references. It performs no notification side effect.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteNotificationReceipt Create(
            IPngJsonCapturePublicationCaptureCompleteNotifier issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
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

            return new PngJsonCapturePublicationCaptureCompleteNotificationReceipt(issuedBy, operation);
        }

        internal IPngJsonCapturePublicationCaptureCompleteNotifier IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _operation.CleanupResult;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _operation.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _operation.Disposition;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _operation.Status;

        /// <summary>
        /// Exception-safe validity: the two held references must be present and
        /// the held operation must remain valid. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return _issuedBy != null && _operation != null && _operation.IsValid;
            }
        }

        /// <summary>
        /// Exception-safe issuance check: requires this receipt to still be
        /// valid and the issuer and operation to be reference-identical to the
        /// supplied values. A same-identity but different operation, a foreign
        /// issuer, or a released owner fails. Never throws.
        /// </summary>
        internal bool IsIssuedFor(
            IPngJsonCapturePublicationCaptureCompleteNotifier notifier,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
        {
            return notifier != null
                && operation != null
                && ReferenceEquals(_issuedBy, notifier)
                && ReferenceEquals(_operation, operation)
                && _operation.IsValid;
        }
    }
}
