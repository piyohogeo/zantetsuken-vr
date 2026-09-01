using System;
using System.Globalization;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable adapter that binds one frozen-Run generic publication plan to
    /// the strict two-artifact Phase 0 PNG-compatible legacy publication plan,
    /// preserving the exact correspondence between both plans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the frozen
    /// publication result and the legacy PNG plan — and has no public
    /// constructor. It duplicates no descriptor, entry, identifier, path,
    /// hash, or lease; every accessor forwards from the held graph. It is
    /// constructed only by the validated builder after the generic plan has
    /// been converted and re-confirmed.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the correspondence without throwing and
    /// without regenerating the legacy plan: it re-checks the frozen result,
    /// the two plans, and the fixed per-frame mapping table by comparing the
    /// held plans. Any forged, replaced, released, or corrupted value converges
    /// to <c>false</c>.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, performs no filesystem
    /// work, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureFrozenRunPublicationPlanBinding
    {
        private readonly CaptureEvidenceFrozenRunPublicationResult _frozenPublicationResult;
        private readonly PngJsonCapturePublicationPlan _legacyPlan;

        internal PngJsonCaptureFrozenRunPublicationPlanBinding(
            CaptureEvidenceFrozenRunPublicationResult frozenPublicationResult,
            PngJsonCapturePublicationPlan legacyPlan)
        {
            _frozenPublicationResult = frozenPublicationResult;
            _legacyPlan = legacyPlan;
        }

        internal CaptureEvidenceFrozenRunPublicationResult FrozenPublicationResult => _frozenPublicationResult;

        internal CapturePublicationPlan GenericPlan => _frozenPublicationResult.Plan;

        internal PngJsonCapturePublicationPlan LegacyPlan => _legacyPlan;

        internal CaptureEvidenceRunFreezeReceipt FreezeReceipt => _frozenPublicationResult.FreezeReceipt;

        internal CaptureFrameDraftRegistry Drafts => _frozenPublicationResult.Drafts;

        internal CaptureArtifactRegistry Artifacts => _frozenPublicationResult.Artifacts;

        internal CaptureRunInitializationSession RunSession => _frozenPublicationResult.RunSession;

        internal CaptureRunRootLayout RootLayout => _frozenPublicationResult.RootLayout;

        internal CaptureRunLockLease LockLease => _frozenPublicationResult.LockLease;

        internal long TestRunId => _frozenPublicationResult.TestRunId;

        internal string RunInitializationId => _frozenPublicationResult.RunInitializationId;

        internal string RunManifestContentHash => _frozenPublicationResult.RunManifestContentHash;

        internal bool IsValid => IsCorrelated(_frozenPublicationResult, _legacyPlan);

        private static bool IsCorrelated(
            CaptureEvidenceFrozenRunPublicationResult frozenPublicationResult,
            PngJsonCapturePublicationPlan legacyPlan)
        {
            if (frozenPublicationResult == null || legacyPlan == null)
            {
                return false;
            }

            if (!frozenPublicationResult.IsValid)
            {
                return false;
            }

            CapturePublicationPlan genericPlan = frozenPublicationResult.Plan;
            if (genericPlan == null || !genericPlan.IsValid)
            {
                return false;
            }

            if (!legacyPlan.IsValid)
            {
                return false;
            }

            if (genericPlan.TestRunId != frozenPublicationResult.TestRunId
                || genericPlan.TestRunId != legacyPlan.TestRunId
                || !string.Equals(genericPlan.RunInitializationId, frozenPublicationResult.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(genericPlan.RunInitializationId, legacyPlan.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(genericPlan.RunManifestContentHash, frozenPublicationResult.RunManifestContentHash, StringComparison.Ordinal)
                || !string.Equals(genericPlan.RunManifestContentHash, legacyPlan.RunManifestContentSha256, StringComparison.Ordinal))
            {
                return false;
            }

            int frameCount = genericPlan.CaptureFrameEvidenceCount;
            if (genericPlan.ArtifactCount != checked(2 * frameCount))
            {
                return false;
            }

            if (legacyPlan.EntryCount != frameCount)
            {
                return false;
            }

            for (int i = 0; i < frameCount; i++)
            {
                if (!FrameCorresponds(genericPlan, legacyPlan, i))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FrameCorresponds(
            CapturePublicationPlan genericPlan,
            PngJsonCapturePublicationPlan legacyPlan,
            int index)
        {
            CaptureFrameEvidenceEntry evidence = genericPlan.GetCaptureFrameEvidence(index);
            PngJsonCapturePublicationPlanEntry legacyEntry = legacyPlan.GetEntry(index);
            if (evidence == null || legacyEntry == null || evidence.CaptureFrameId != legacyEntry.CaptureFrameId)
            {
                return false;
            }

            if (evidence.ArtifactCount != 2)
            {
                return false;
            }

            string id = evidence.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
            string imageId = "frame/" + id + "/image";
            string metadataId = "frame/" + id + "/metadata";

            if (!string.Equals(evidence.GetArtifactId(0), imageId, StringComparison.Ordinal)
                || !string.Equals(evidence.GetArtifactId(1), metadataId, StringComparison.Ordinal))
            {
                return false;
            }

            CaptureArtifactDescriptor image = FindDescriptor(genericPlan, imageId);
            CaptureArtifactDescriptor metadata = FindDescriptor(genericPlan, metadataId);
            if (image == null || metadata == null)
            {
                return false;
            }

            if (!IsImageDescriptor(image, id) || !IsMetadataDescriptor(metadata, id))
            {
                return false;
            }

            return image.ByteLength == legacyEntry.PngByteLength
                && metadata.ByteLength == legacyEntry.SidecarByteLength
                && string.Equals(image.ContentHash, legacyEntry.PngContentSha256, StringComparison.Ordinal)
                && string.Equals(metadata.ContentHash, legacyEntry.SidecarContentSha256, StringComparison.Ordinal)
                && string.Equals(image.StagingRelativePath, legacyEntry.PngStagingRelativePath, StringComparison.Ordinal)
                && string.Equals(image.FinalRelativePath, legacyEntry.PngFinalRelativePath, StringComparison.Ordinal)
                && string.Equals(metadata.StagingRelativePath, legacyEntry.SidecarStagingRelativePath, StringComparison.Ordinal)
                && string.Equals(metadata.FinalRelativePath, legacyEntry.SidecarFinalRelativePath, StringComparison.Ordinal);
        }

        private static bool IsImageDescriptor(CaptureArtifactDescriptor descriptor, string id)
        {
            return descriptor.ArtifactKind == CaptureArtifactKind.FrameImage
                && string.Equals(descriptor.FormatId, "image/png", StringComparison.Ordinal)
                && descriptor.FormatVersion == 1
                && string.Equals(descriptor.StagingRelativePath, "frames/" + id + ".png.stage", StringComparison.Ordinal)
                && string.Equals(descriptor.FinalRelativePath, "frames/" + id + ".png", StringComparison.Ordinal);
        }

        private static bool IsMetadataDescriptor(CaptureArtifactDescriptor descriptor, string id)
        {
            return descriptor.ArtifactKind == CaptureArtifactKind.FrameMetadata
                && string.Equals(descriptor.FormatId, "application/vnd.zantetsu.capture-frame+json", StringComparison.Ordinal)
                && descriptor.FormatVersion == 2
                && string.Equals(descriptor.StagingRelativePath, "frames/" + id + ".json.stage", StringComparison.Ordinal)
                && string.Equals(descriptor.FinalRelativePath, "frames/" + id + ".json", StringComparison.Ordinal);
        }

        private static CaptureArtifactDescriptor FindDescriptor(CapturePublicationPlan genericPlan, string artifactId)
        {
            int lo = 0;
            int hi = genericPlan.ArtifactCount - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                int compare = string.CompareOrdinal(genericPlan.GetArtifact(mid).ArtifactId, artifactId);
                if (compare == 0)
                {
                    return genericPlan.GetArtifact(mid);
                }

                if (compare < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return null;
        }
    }
}
