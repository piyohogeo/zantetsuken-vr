using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a pure artifact recovery classification: the
    /// artifact inspection snapshot it classified and the fixed disposition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposition is recomputed from the snapshot at construction, so no
    /// external caller can hand in a contradicting value. <see cref="IsValid"/>
    /// recomputes the same correlation without throwing, so a decision whose
    /// snapshot's lease has been released, or whose held value was forged,
    /// becomes invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryDecision
    {
        private readonly CaptureRunPublicationArtifactInspectionSnapshot _snapshot;
        private readonly CaptureRunPublicationArtifactRecoveryDisposition _disposition;

        internal CaptureRunPublicationArtifactRecoveryDecision(
            CaptureRunPublicationArtifactInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            _disposition = CaptureRunPublicationArtifactRecoveryClassifier.ComputeDisposition(snapshot);
            _snapshot = snapshot;
        }

        internal CaptureRunPublicationArtifactInspectionSnapshot Snapshot => _snapshot;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _disposition;

        internal CaptureRunPublicationArtifactInspectionOperation Operation => _snapshot.Operation;

        internal CaptureRunPublicationRecoveryDecision PublicationDecision => _snapshot.Decision;

        internal CapturePublicationPlan AuthoritativePlan => _snapshot.Plan;

        internal CaptureRunRootLayout RootLayout => _snapshot.Operation.RootLayout;

        internal long TestRunId => _snapshot.Operation.TestRunId;

        internal string RunInitializationId => _snapshot.Operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                if (_snapshot == null || !_snapshot.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryDisposition expected =
                    CaptureRunPublicationArtifactRecoveryClassifier.ComputeDisposition(_snapshot);

                return _disposition == expected;
            }
        }
    }
}
