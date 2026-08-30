using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Run-lifecycle entry point that selects the format-neutral publication
    /// plan store for both post-freeze persistence and restart recovery.
    /// Legacy PngJson publication contracts are not consulted here.
    /// </summary>
    internal sealed class CaptureEvidenceRunPublicationCoordinator
    {
        private readonly CaptureArtifactFileStore _store;
        private readonly CaptureEvidencePublicationCoordinator _publication;
        private readonly CapturePublicationRecoveryCoordinator _recovery;
        private readonly object _recoveryReceiptAuthority;

        internal CaptureEvidenceRunPublicationCoordinator(CaptureArtifactFileStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _publication = new CaptureEvidencePublicationCoordinator(store);
            _recovery = new CapturePublicationRecoveryCoordinator(store);
            _recoveryReceiptAuthority = new object();
        }

        internal CapturePublicationPlanWriteReceipt PersistFrozenRun(
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            string runManifestContentHash)
        {
            if (freezeReceipt == null) throw new ArgumentNullException(nameof(freezeReceipt));
            if (!freezeReceipt.IsValid) throw new ArgumentException("Freeze receipt must remain valid.", nameof(freezeReceipt));
            if (!ReferenceEquals(freezeReceipt.RootLayout, _store.RootLayout))
                throw new ArgumentException("Freeze receipt and store must share the exact Run root layout.", nameof(freezeReceipt));
            return _publication.BuildAndPersist(
                freezeReceipt.Drafts,
                freezeReceipt.Artifacts,
                freezeReceipt.RunInitializationId,
                runManifestContentHash);
        }

        internal CaptureEvidenceRunRecoveryInspectionReceipt RecoverAfterRestart(
            CaptureRunInitializationOpenOutcome openOutcome,
            int maximumCanonicalByteCount)
        {
            RequireRecoveryOutcome(openOutcome);
            CapturePublicationRecoverySnapshot snapshot = _recovery.InspectPersisted(_store, maximumCanonicalByteCount);
            if (!IsRecoveryContextFor(openOutcome, snapshot))
                throw new InvalidOperationException("Recovered snapshot is not correlated with the locked Run.");
            return new CaptureEvidenceRunRecoveryInspectionReceipt(
                this, _recoveryReceiptAuthority, openOutcome, snapshot);
        }

        internal CapturePublicationRecoveryDisposition ContinueRecovery(
            CaptureEvidenceRunRecoveryInspectionReceipt inspectionReceipt)
        {
            if (inspectionReceipt == null) throw new ArgumentNullException(nameof(inspectionReceipt));
            if (!inspectionReceipt.IsIssuedFor(this))
                throw new ArgumentException("Inspection receipt must remain valid and be issued by this coordinator.", nameof(inspectionReceipt));
            return _recovery.ExecuteMissing(inspectionReceipt.Snapshot);
        }

        internal bool IsRecoveryContextFor(
            CaptureRunInitializationOpenOutcome openOutcome,
            CapturePublicationRecoverySnapshot snapshot)
        {
            if (openOutcome == null || snapshot == null || !snapshot.IsValid) return false;
            if (!openOutcome.IsCreated || !openOutcome.IsValid
                || openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                || !ReferenceEquals(openOutcome.RootLayout, _store.RootLayout)) return false;
            CapturePublicationPlan plan = snapshot.Plan;
            return plan != null
                && plan.IsValid
                && plan.TestRunId == openOutcome.TestRunId
                && plan.TestRunId == _store.RootLayout.TestRunId;
        }

        internal bool IsRecoveryReceiptAuthority(object authority) =>
            ReferenceEquals(_recoveryReceiptAuthority, authority);

        private void RequireRecoveryOutcome(CaptureRunInitializationOpenOutcome openOutcome)
        {
            if (openOutcome == null) throw new ArgumentNullException(nameof(openOutcome));
            if (!openOutcome.IsCreated || !openOutcome.IsValid
                || openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
                throw new ArgumentException("Open outcome must hold publication recovery and the OS Run lock.", nameof(openOutcome));
            if (!ReferenceEquals(openOutcome.RootLayout, _store.RootLayout))
                throw new ArgumentException("Open outcome and store must share the exact Run root layout.", nameof(openOutcome));
        }
    }
}
