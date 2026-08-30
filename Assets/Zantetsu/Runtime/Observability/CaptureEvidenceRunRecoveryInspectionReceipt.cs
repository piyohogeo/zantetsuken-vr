using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-owning proof that one generic recovery snapshot was loaded by the
    /// issuing Run publication coordinator while the exact correlated
    /// PublicationRecoveryRequired outcome still held the OS Run lock.
    /// </summary>
    internal sealed class CaptureEvidenceRunRecoveryInspectionReceipt
    {
        private readonly CaptureEvidenceRunPublicationCoordinator _issuedBy;
        private readonly object _authority;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CapturePublicationRecoverySnapshot _snapshot;

        internal CaptureEvidenceRunRecoveryInspectionReceipt(
            CaptureEvidenceRunPublicationCoordinator issuedBy,
            object authority,
            CaptureRunInitializationOpenOutcome openOutcome,
            CapturePublicationRecoverySnapshot snapshot)
        {
            _issuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _openOutcome = openOutcome ?? throw new ArgumentNullException(nameof(openOutcome));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (!issuedBy.IsRecoveryReceiptAuthority(authority)
                || !issuedBy.IsRecoveryContextFor(openOutcome, snapshot))
                throw new ArgumentException("Outcome, store, and snapshot must describe the same locked Run.", nameof(snapshot));
        }

        internal CaptureEvidenceRunPublicationCoordinator IssuedBy => _issuedBy;
        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;
        internal CapturePublicationRecoverySnapshot Snapshot => _snapshot;
        internal long TestRunId => _snapshot.Plan.TestRunId;
        internal bool IsValid => _issuedBy != null
            && _issuedBy.IsRecoveryReceiptAuthority(_authority)
            && _issuedBy.IsRecoveryContextFor(_openOutcome, _snapshot);

        internal bool IsIssuedFor(CaptureEvidenceRunPublicationCoordinator coordinator) =>
            ReferenceEquals(_issuedBy, coordinator) && IsValid;
    }
}
