using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of a completed recovery inspection: which inspector
    /// produced it, which operation it observed, and the two per-root
    /// observations for staging and final.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot owns and disposes nothing — neither the lease, the roots,
    /// nor the markers. It performs no collision classification and no mutual
    /// marker binding. <see cref="IsValid"/> recomputes the correlation checks
    /// from the held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryInspectionSnapshot
    {
        private readonly ICaptureRunInitializationRecoveryInspector _issuedBy;
        private readonly CaptureRunInitializationRecoveryInspectionOperation _operation;
        private readonly CaptureRunInitializationRootObservation _staging;
        private readonly CaptureRunInitializationRootObservation _final;

        internal CaptureRunInitializationRecoveryInspectionSnapshot(
            ICaptureRunInitializationRecoveryInspector issuedBy,
            CaptureRunInitializationRecoveryInspectionOperation operation,
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (staging == null)
            {
                throw new ArgumentNullException(nameof(staging));
            }

            if (final == null)
            {
                throw new ArgumentNullException(nameof(final));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Inspection operation must be valid.", nameof(operation));
            }

            if (!staging.IsValid)
            {
                throw new ArgumentException("Staging observation must be internally consistent.", nameof(staging));
            }

            if (!final.IsValid)
            {
                throw new ArgumentException("Final observation must be internally consistent.", nameof(final));
            }

            if (staging.RootRole != CaptureRunRootRole.Staging)
            {
                throw new ArgumentException("Staging observation must have the Staging role.", nameof(staging));
            }

            if (final.RootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentException("Final observation must have the Final role.", nameof(final));
            }

            _issuedBy = issuedBy;
            _operation = operation;
            _staging = staging;
            _final = final;
        }

        internal ICaptureRunInitializationRecoveryInspector IssuedBy => _issuedBy;

        internal CaptureRunInitializationRecoveryInspectionOperation Operation => _operation;

        internal CaptureRunInitializationRootObservation Staging => _staging;

        internal CaptureRunInitializationRootObservation Final => _final;

        internal bool IsValid =>
            _issuedBy != null
            && _operation != null
            && _staging != null
            && _final != null
            && _operation.IsValid
            && _staging.IsValid
            && _final.IsValid
            && _staging.RootRole == CaptureRunRootRole.Staging
            && _final.RootRole == CaptureRunRootRole.Final;
    }
}
