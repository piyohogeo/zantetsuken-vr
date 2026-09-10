using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one pure Phase 0.11 NVENC publication recovery
    /// classification: the snapshot it classified, the fixed disposition, and,
    /// only for
    /// <see cref="NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired"/>,
    /// the authoritative plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposition and the authoritative plan are recomputed from the
    /// snapshot at construction, so no caller can hand in a contradicting
    /// combination. The authoritative plan is the very same
    /// <see cref="CapturePublicationPlan"/> reference the snapshot already
    /// held: nothing is copied, re-serialized, or re-hashed, and every other
    /// disposition exposes none.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes the same correlation without throwing,
    /// so a decision whose snapshot's lock has gone becomes invalid.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryDecision
    {
        private readonly NvencRunPublicationRecoveryInspectionSnapshot _snapshot;
        private readonly NvencRunPublicationRecoveryDisposition _disposition;
        private readonly CapturePublicationPlan _authoritativePlan;

        internal NvencRunPublicationRecoveryDecision(
            NvencRunPublicationRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            NvencRunPublicationRecoveryDisposition disposition =
                NvencRunPublicationRecoveryClassifier.ComputeDisposition(
                    snapshot, out CapturePublicationPlan authoritativePlan);

            if (disposition == NvencRunPublicationRecoveryDisposition.None)
            {
                throw new InvalidOperationException(
                    "The classifier must select a defined recovery disposition.");
            }

            _snapshot = snapshot;
            _disposition = disposition;
            _authoritativePlan = authoritativePlan;
        }

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _snapshot;

        internal NvencRunPublicationRecoveryDisposition Disposition => _disposition;

        /// <summary>
        /// The exact plan reference the snapshot held, published only for a
        /// recoverable Run and null for every other disposition.
        /// </summary>
        internal CapturePublicationPlan AuthoritativePlan => _authoritativePlan;

        internal NvencRunPublicationRecoveryInspectionOperation Operation => _snapshot.Operation;

        internal CaptureRunRootLayout RootLayout => _snapshot.RootLayout;

        internal long TestRunId => _snapshot.TestRunId;

        internal string RunInitializationId => _snapshot.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_snapshot == null || !_snapshot.IsValid
                        || _disposition == NvencRunPublicationRecoveryDisposition.None)
                    {
                        return false;
                    }

                    NvencRunPublicationRecoveryDisposition recomputed =
                        NvencRunPublicationRecoveryClassifier.ComputeDisposition(
                            _snapshot, out CapturePublicationPlan recomputedPlan);

                    return recomputed == _disposition
                        && ReferenceEquals(recomputedPlan, _authoritativePlan);
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
