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

        internal CaptureEvidenceRunPublicationCoordinator(CaptureArtifactFileStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _publication = new CaptureEvidencePublicationCoordinator(store);
            _recovery = new CapturePublicationRecoveryCoordinator(store);
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

        internal CapturePublicationRecoverySnapshot RecoverAfterRestart(
            CaptureRunInitializationOpenOutcome openOutcome,
            int maximumCanonicalByteCount)
        {
            RequireRecoveryOutcome(openOutcome);
            return _recovery.InspectPersisted(_store, maximumCanonicalByteCount);
        }

        internal CapturePublicationRecoveryDisposition ContinueRecovery(
            CaptureRunInitializationOpenOutcome openOutcome,
            CapturePublicationRecoverySnapshot snapshot)
        {
            RequireRecoveryOutcome(openOutcome);
            return _recovery.ExecuteMissing(snapshot);
        }

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
