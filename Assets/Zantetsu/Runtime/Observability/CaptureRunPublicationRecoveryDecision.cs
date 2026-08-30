using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a pure publication recovery classification: the
    /// snapshot it classified, the fixed disposition, and, when authoritative,
    /// the authoritative plan held by reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposition and authoritative plan are recomputed from the snapshot
    /// at construction, so no external caller can hand in a contradicting
    /// combination. <see cref="IsValid"/> recomputes the same correlation
    /// without throwing, so a decision whose snapshot's lease has been
    /// released, or whose held values were forged, becomes invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationRecoveryDecision
    {
        private readonly CaptureRunPublicationRecoveryInspectionSnapshot _snapshot;
        private readonly CaptureRunPublicationRecoveryDisposition _disposition;
        private readonly CapturePublicationPlan _authoritativePlan;

        internal CaptureRunPublicationRecoveryDecision(
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            CaptureRunPublicationRecoveryDisposition disposition = CaptureRunPublicationRecoveryClassifier.ComputeDisposition(
                snapshot, out CapturePublicationPlan authoritativePlan);

            _snapshot = snapshot;
            _disposition = disposition;
            _authoritativePlan = authoritativePlan;
        }

        internal CaptureRunPublicationRecoveryInspectionSnapshot Snapshot => _snapshot;

        internal CaptureRunPublicationRecoveryDisposition Disposition => _disposition;

        internal CapturePublicationPlan AuthoritativePlan => _authoritativePlan;

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

                CaptureRunPublicationRecoveryDisposition expected = CaptureRunPublicationRecoveryClassifier.ComputeDisposition(
                    _snapshot, out CapturePublicationPlan expectedPlan);

                return _disposition == expected && ReferenceEquals(_authoritativePlan, expectedPlan);
            }
        }
    }
}
