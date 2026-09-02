using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one cleanup operation: which backend issued
    /// it and which operation it executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type owns and disposes nothing and performs no filesystem work. It
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes the held checks from the values without
    /// throwing, including after the lease has been released.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryCleanupReceipt
    {
        private readonly ICaptureRunInitializationRecoveryCleanupBackend _issuedBy;
        private readonly CaptureRunInitializationRecoveryCleanupOperation _operation;

        internal CaptureRunInitializationRecoveryCleanupReceipt(
            ICaptureRunInitializationRecoveryCleanupBackend issuedBy,
            CaptureRunInitializationRecoveryCleanupOperation operation)
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
                throw new ArgumentException("Cleanup operation must be valid.", nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunInitializationRecoveryCleanupBackend IssuedBy => _issuedBy;

        internal CaptureRunInitializationRecoveryCleanupOperation Operation => _operation;

        internal CaptureRunInitializationRecoveryActionPlan ActionPlan => _operation.ActionPlan;

        internal CaptureRunMarkerPathSet MarkerPaths => _operation.MarkerPaths;

        internal int StepIndex => _operation.StepIndex;

        internal CaptureRunInitializationRecoveryStep Step => _operation.Step;

        internal CaptureRunInitializationRecoveryAction Action => _operation.Action;

        internal CaptureRunRootRole RootRole => _operation.RootRole;

        internal CaptureRunMarkerKind MarkerKind => _operation.MarkerKind;

        internal string TargetPath => _operation.TargetPath;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

        internal long TestRunId => _operation.TestRunId;

        internal bool IsValid =>
            _issuedBy != null
            && _operation != null
            && _operation.IsValid;

        internal bool IsIssuedFor(
            ICaptureRunInitializationRecoveryCleanupBackend backend,
            CaptureRunInitializationRecoveryCleanupOperation operation)
        {
            return backend != null
                && operation != null
                && ReferenceEquals(_issuedBy, backend)
                && ReferenceEquals(_operation, operation)
                && _operation.IsValid;
        }
    }
}
