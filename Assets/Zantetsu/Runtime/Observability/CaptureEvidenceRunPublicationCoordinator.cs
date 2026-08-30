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
        private readonly ICapturePublicationPlanStore _planStore;
        private readonly CaptureEvidencePublicationCoordinator _publication;
        private readonly CapturePublicationRecoveryCoordinator _recovery;

        internal CaptureEvidenceRunPublicationCoordinator(
            ICapturePublicationPlanStore planStore,
            ICaptureArtifactStore artifactStore)
        {
            _planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
            if (artifactStore == null) throw new ArgumentNullException(nameof(artifactStore));
            _publication = new CaptureEvidencePublicationCoordinator(planStore);
            _recovery = new CapturePublicationRecoveryCoordinator(artifactStore);
        }

        internal CapturePublicationPlanWriteReceipt PersistFrozenRun(
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts,
            string runInitializationId,
            string runManifestContentHash)
        {
            return _publication.BuildAndPersist(
                drafts,
                artifacts,
                runInitializationId,
                runManifestContentHash);
        }

        internal CapturePublicationRecoverySnapshot RecoverAfterRestart(int maximumCanonicalByteCount)
        {
            return _recovery.InspectPersisted(_planStore, maximumCanonicalByteCount);
        }

        internal CapturePublicationRecoveryDisposition ContinueRecovery(
            CapturePublicationRecoverySnapshot snapshot)
        {
            return _recovery.ExecuteMissing(snapshot);
        }
    }
}
