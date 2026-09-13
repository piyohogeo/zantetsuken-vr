using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one cleanup operation: which backend issued
    /// it and which operation it executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type holds exactly two readonly references — the issuing backend and
    /// the cleanup operation. What was cleaned up is read from
    /// <see cref="Operation"/>; the receipt restates none of its values and
    /// duplicates no path, marker, plan, Run identity, or lock evidence as a
    /// field of its own.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing — neither the issuing backend nor
    /// the operation — and performs no filesystem work. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
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
