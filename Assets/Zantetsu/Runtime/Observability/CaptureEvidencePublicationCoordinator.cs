using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread bridge from frozen generic Draft/Artifact registries to the
    /// durable generic publication-plan authority.
    /// </summary>
    internal sealed class CaptureEvidencePublicationCoordinator
    {
        private readonly ICapturePublicationPlanStore _planStore;

        internal CaptureEvidencePublicationCoordinator(ICapturePublicationPlanStore planStore)
        {
            _planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        }

        internal CapturePublicationPlanWriteReceipt BuildAndPersist(
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts,
            string runInitializationId,
            string runManifestContentHash)
        {
            CapturePublicationPlan plan = CapturePublicationPlanBuilder.Build(
                drafts,
                artifacts,
                runInitializationId,
                runManifestContentHash);
            CapturePublicationPlanWriteReceipt receipt = _planStore.WritePlan(plan);
            if (receipt == null || !receipt.IsIssuedFor(_planStore, plan))
                throw new InvalidOperationException("Plan store returned an invalid receipt.");
            return receipt;
        }
    }
}
