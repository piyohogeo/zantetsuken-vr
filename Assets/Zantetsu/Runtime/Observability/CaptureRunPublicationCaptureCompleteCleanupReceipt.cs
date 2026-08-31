using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one capture-complete cleanup operation:
    /// which backend issued it and which operation it executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type owns and disposes nothing and performs no filesystem work. It
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// It holds no path, hash, byte count, handle, or array in its own fields.
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks from the values without throwing, including after the lease has
    /// been released or a nested value was forged.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupReceipt
    {
        private readonly ICaptureRunPublicationCaptureCompleteCleanupBackend _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteCleanupOperation _operation;

        internal CaptureRunPublicationCaptureCompleteCleanupReceipt(
            ICaptureRunPublicationCaptureCompleteCleanupBackend issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupOperation operation)
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

        internal ICaptureRunPublicationCaptureCompleteCleanupBackend IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteCleanupOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _operation.ActionPlan;

        internal int StepIndex => _operation.StepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupStep Step => _operation.Step;

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => _operation.Action;

        internal int EntryIndex => _operation.EntryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => _operation.ArtifactKind;

        internal string TargetPath => _operation.TargetPath;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockLease LockLease => _operation.LockLease;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                return _issuedBy != null && _operation != null && _operation.IsValid;
            }
        }

        internal bool IsIssuedFor(
            ICaptureRunPublicationCaptureCompleteCleanupBackend backend,
            CaptureRunPublicationCaptureCompleteCleanupOperation operation)
        {
            return backend != null
                && operation != null
                && ReferenceEquals(_issuedBy, backend)
                && ReferenceEquals(_operation, operation)
                && _operation.IsValid;
        }
    }
}
