using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure in-memory NVENC Run publication plan builder. It reduces the exact
    /// finalized chunk result — its exact artifact descriptor and frame
    /// relation — plus the exact session issue and a caller-supplied lowercase
    /// SHA-256 run manifest hash into a single
    /// <see cref="CapturePublicationPlan"/>. The artifact descriptor is the
    /// exact descriptor already fixed by finalization, so its format, path,
    /// length, and hash are never recomputed or rewritten, and the manifest
    /// hash is used verbatim.
    /// </summary>
    /// <remarks>
    /// This type is stateless and performs no filesystem, store, codec,
    /// hash recomputation, thread, or task work. It rejects a nulled,
    /// foreign, non-FrameSequence, or pending-path descriptor, and a frame
    /// relation outside the fixed 1..120 accepted range.
    /// </remarks>
    internal static class NvencRunPublicationPlanBuilder
    {
        internal static CapturePublicationPlan Build(
            NvencChunkFinalizationResult finalizationResult,
            CaptureRunInitializationSessionIssue sessionIssue,
            string runManifestContentHash)
        {
            if (finalizationResult == null)
            {
                throw new ArgumentNullException(nameof(finalizationResult));
            }

            if (sessionIssue == null)
            {
                throw new ArgumentNullException(nameof(sessionIssue));
            }

            if (!finalizationResult.IsValid)
            {
                throw new ArgumentException(
                    "Finalization result must be valid.", nameof(finalizationResult));
            }

            if (!sessionIssue.IsValid)
            {
                throw new ArgumentException(
                    "Session issue must be valid and hold a live Ownership Lease.", nameof(sessionIssue));
            }

            CaptureArtifactDescriptor descriptor = finalizationResult.Descriptor;
            if (descriptor == null || !descriptor.IsValid
                || descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
            {
                throw new ArgumentException(
                    "Finalization result must carry a valid FrameSequence artifact descriptor.",
                    nameof(finalizationResult));
            }

            if (ContainsPendingPath(descriptor.StagingRelativePath)
                || ContainsPendingPath(descriptor.FinalRelativePath))
            {
                throw new ArgumentException(
                    "Finalization result descriptor must not reference a pending path.",
                    nameof(finalizationResult));
            }

            CaptureArtifactFrameRelation relation = finalizationResult.FrameRelation;
            if (relation == null || !relation.IsValid
                || relation.Count < 1
                || relation.Count > NvencBringUpProfileV1.CadenceTickCount)
            {
                throw new ArgumentException(
                    "Finalization result frame relation must hold between 1 and 120 frame ids.",
                    nameof(finalizationResult));
            }

            CaptureRunInitializationSession session = sessionIssue.Session;
            if (session == null || !session.IsValid)
            {
                throw new ArgumentException(
                    "Session issue must hold a valid session.", nameof(sessionIssue));
            }

            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[] { descriptor };
            string[] artifactIds = new string[] { descriptor.ArtifactId };
            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[relation.Count];
            for (int i = 0; i < relation.Count; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(relation.GetCaptureFrameId(i), artifactIds);
            }

            return new CapturePublicationPlan(
                session.TestRunId,
                session.RunInitializationId,
                runManifestContentHash,
                descriptors,
                entries);
        }

        private static bool ContainsPendingPath(string path)
        {
            return path != null
                && path.IndexOf(".partial", StringComparison.Ordinal) >= 0;
        }
    }
}
