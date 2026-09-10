using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one pure Phase 0.11 NVENC Capture Index recovery
    /// classification: the snapshot it classified, the fixed disposition, and,
    /// only for
    /// <see cref="NvencRunCaptureIndexRecoveryDisposition.CommitRequired"/>,
    /// the existing commit mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposition and the commit mode are recomputed from the snapshot at
    /// construction, so no caller can hand in a contradicting combination.
    /// CaptureComplete and collision carry
    /// <see cref="CaptureRunCaptureIndexCommitMode.None"/>: a leftover
    /// temporary is already an observed fact in the snapshot and is never
    /// duplicated here as a mode or a flag.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file,
    /// serializes and hashes nothing, releases no lock, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes the same classification without
    /// throwing, so a decision whose snapshot's lock has gone becomes invalid.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryDecision
    {
        private readonly NvencRunCaptureIndexRecoveryInspectionSnapshot _snapshot;
        private readonly NvencRunCaptureIndexRecoveryDisposition _disposition;
        private readonly CaptureRunCaptureIndexCommitMode _commitMode;

        internal NvencRunCaptureIndexRecoveryDecision(
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            NvencRunCaptureIndexRecoveryDisposition disposition =
                NvencRunCaptureIndexRecoveryClassifier.ComputeDisposition(
                    snapshot, out CaptureRunCaptureIndexCommitMode commitMode);

            if (disposition == NvencRunCaptureIndexRecoveryDisposition.None)
            {
                throw new InvalidOperationException(
                    "The classifier must select a defined Capture Index recovery disposition.");
            }

            _snapshot = snapshot;
            _disposition = disposition;
            _commitMode = commitMode;
        }

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot Snapshot => _snapshot;

        internal NvencRunCaptureIndexRecoveryDisposition Disposition => _disposition;

        /// <summary>
        /// How the commit must treat the temporary, and
        /// <see cref="CaptureRunCaptureIndexCommitMode.None"/> for every
        /// disposition that commits nothing.
        /// </summary>
        internal CaptureRunCaptureIndexCommitMode CommitMode => _commitMode;

        internal NvencRunCaptureIndexRecoveryInspectionOperation Operation => _snapshot.Operation;

        internal CapturePublicationPlan AuthoritativePlan => _snapshot.AuthoritativePlan;

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
                        || _disposition == NvencRunCaptureIndexRecoveryDisposition.None)
                    {
                        return false;
                    }

                    NvencRunCaptureIndexRecoveryDisposition recomputed =
                        NvencRunCaptureIndexRecoveryClassifier.ComputeDisposition(
                            _snapshot, out CaptureRunCaptureIndexCommitMode recomputedMode);

                    return recomputed == _disposition && recomputedMode == _commitMode;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
