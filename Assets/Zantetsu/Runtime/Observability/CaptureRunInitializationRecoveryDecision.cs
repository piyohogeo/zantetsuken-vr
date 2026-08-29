using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable recovery decision produced by the pure classifier for one
    /// observed Capture Run: the observation snapshot, the selected disposition,
    /// and the expected marker binding when one exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fresh and collision dispositions carry no binding; the completion,
    /// already-initialized, and publication dispositions carry the expected
    /// binding that must match the snapshot's root layout. <see cref="IsValid"/>
    /// recomputes these checks from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing — neither the snapshot, the binding,
    /// nor the lease. It performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryDecision
    {
        private readonly CaptureRunInitializationRecoveryInspectionSnapshot _snapshot;
        private readonly CaptureRunInitializationRecoveryDisposition _disposition;
        private readonly CaptureRunMarkerBinding _expectedBinding;

        internal CaptureRunInitializationRecoveryDecision(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            CaptureRunInitializationRecoveryDisposition disposition,
            CaptureRunMarkerBinding expectedBinding)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            if (!IsDefinedDisposition(disposition))
            {
                throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Disposition must be a defined recovery disposition.");
            }

            CaptureRunInitializationRecoveryDisposition computed = CaptureRunInitializationRecoveryClassifier.Determine(
                snapshot,
                out CaptureRunMarkerBinding computedBinding);

            if (disposition != computed)
            {
                throw new ArgumentException("Disposition must match the one the snapshot implies.", nameof(disposition));
            }

            bool bindingRequired = RequiresBinding(disposition);
            if (!bindingRequired)
            {
                if (expectedBinding != null)
                {
                    throw new ArgumentException("Fresh and collision dispositions must not carry an expected binding.", nameof(expectedBinding));
                }
            }
            else
            {
                if (expectedBinding == null)
                {
                    throw new ArgumentException("Completion, already-initialized, and publication dispositions must carry an expected binding.", nameof(expectedBinding));
                }

                if (!CaptureRunInitializationRecoveryClassifier.BindingMatches(computedBinding, expectedBinding))
                {
                    throw new ArgumentException("Expected binding must match the observed markers.", nameof(expectedBinding));
                }
            }

            _snapshot = snapshot;
            _disposition = disposition;
            _expectedBinding = expectedBinding;
        }

        internal CaptureRunInitializationRecoveryInspectionSnapshot Snapshot => _snapshot;

        internal CaptureRunInitializationRecoveryDisposition Disposition => _disposition;

        internal CaptureRunMarkerBinding ExpectedBinding => _expectedBinding;

        internal CaptureRunRootLayout RootLayout => _snapshot.Operation.RootLayout;

        internal long TestRunId => RootLayout.TestRunId;

        internal string RunInitializationId => _expectedBinding != null ? _expectedBinding.RunInitializationId : null;

        internal bool IsValid
        {
            get
            {
                if (_snapshot == null || !_snapshot.IsValid || !IsDefinedDisposition(_disposition))
                {
                    return false;
                }

                return CaptureRunInitializationRecoveryClassifier.IsCorrelated(_snapshot, _disposition, _expectedBinding);
            }
        }

        private static bool IsDefinedDisposition(CaptureRunInitializationRecoveryDisposition disposition)
        {
            return disposition >= CaptureRunInitializationRecoveryDisposition.StartFresh
                && disposition <= CaptureRunInitializationRecoveryDisposition.RunRootCollision;
        }

        private static bool RequiresBinding(CaptureRunInitializationRecoveryDisposition disposition)
        {
            return disposition == CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization
                || disposition == CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers
                || disposition == CaptureRunInitializationRecoveryDisposition.AlreadyInitialized
                || disposition == CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery;
        }
    }
}
