using System;
using System.Globalization;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless adapter that converts a validated frozen-Run generic
    /// publication plan into the strict two-artifact Phase 0 PNG-compatible
    /// legacy plan, or rejects it. The generic plan is never mutated and no
    /// legacy value is injected from outside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Build"/> performs the full validation and conversion exactly
    /// once: it validates the frozen result and its generic plan, correlates
    /// run identity and manifest hash, verifies the checked two-artifact-per-
    /// frame count, inspects every frame and descriptor against the fixed PNG
    /// and sidecar schema using a binary search over the ordinal-sorted
    /// descriptors (never a quadratic scan), builds the exact-length legacy
    /// entry array, constructs the legacy plan, re-confirms the correspondence,
    /// and only then constructs the binding. Descriptor lookup is O(log n) per
    /// frame, so the whole conversion is O(n log n).
    /// </para>
    /// <para>
    /// This type holds no fields and performs no filesystem, codec, hashing,
    /// or logging work.
    /// </para>
    /// </remarks>
    internal static class PngJsonCaptureFrozenRunPublicationPlanBindingBuilder
    {
        internal static PngJsonCaptureFrozenRunPublicationPlanBinding Build(
            CaptureEvidenceFrozenRunPublicationResult frozenPublicationResult)
        {
            if (frozenPublicationResult == null)
            {
                throw new ArgumentNullException(nameof(frozenPublicationResult));
            }

            if (!frozenPublicationResult.IsValid)
            {
                throw new ArgumentException("Frozen publication result must remain valid.", nameof(frozenPublicationResult));
            }

            CapturePublicationPlan genericPlan = frozenPublicationResult.Plan;
            if (genericPlan == null || !genericPlan.IsValid)
            {
                throw new ArgumentException("Frozen publication result must hold a valid generic plan.", nameof(frozenPublicationResult));
            }

            if (genericPlan.TestRunId != frozenPublicationResult.TestRunId
                || !string.Equals(genericPlan.RunInitializationId, frozenPublicationResult.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(genericPlan.RunManifestContentHash, frozenPublicationResult.RunManifestContentHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("Generic plan must correlate with the frozen publication result.", nameof(frozenPublicationResult));
            }

            int frameCount = genericPlan.CaptureFrameEvidenceCount;
            if (genericPlan.ArtifactCount != checked(2 * frameCount))
            {
                throw new ArgumentException("Generic plan must have exactly two artifacts per frame.", nameof(frozenPublicationResult));
            }

            PngJsonCapturePublicationPlanEntry[] entries = new PngJsonCapturePublicationPlanEntry[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                entries[i] = BuildEntry(genericPlan, i);
            }

            PngJsonCapturePublicationPlan legacyPlan = new PngJsonCapturePublicationPlan(
                genericPlan.TestRunId,
                genericPlan.RunInitializationId,
                genericPlan.RunManifestContentHash,
                entries);

            if (legacyPlan.EntryCount != frameCount
                || legacyPlan.TestRunId != genericPlan.TestRunId
                || !string.Equals(legacyPlan.RunInitializationId, genericPlan.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(legacyPlan.RunManifestContentSha256, genericPlan.RunManifestContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Legacy plan does not correspond to the generic plan.");
            }

            return new PngJsonCaptureFrozenRunPublicationPlanBinding(frozenPublicationResult, legacyPlan);
        }

        private static PngJsonCapturePublicationPlanEntry BuildEntry(
            CapturePublicationPlan genericPlan,
            int index)
        {
            CaptureFrameEvidenceEntry evidence = genericPlan.GetCaptureFrameEvidence(index);
            if (evidence == null || evidence.ArtifactCount != 2)
            {
                throw new ArgumentException("Each frame must reference exactly two artifacts.");
            }

            string id = evidence.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
            string imageId = "frame/" + id + "/image";
            string metadataId = "frame/" + id + "/metadata";

            if (!string.Equals(evidence.GetArtifactId(0), imageId, StringComparison.Ordinal)
                || !string.Equals(evidence.GetArtifactId(1), metadataId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Frame artifacts must be the fixed image and metadata pair in ordinal order.");
            }

            CaptureArtifactDescriptor image = FindDescriptor(genericPlan, imageId);
            CaptureArtifactDescriptor metadata = FindDescriptor(genericPlan, metadataId);
            if (image == null || metadata == null)
            {
                throw new ArgumentException("Frame artifacts must resolve to descriptors.");
            }

            if (!IsImageDescriptor(image, id))
            {
                throw new ArgumentException("Image descriptor must match the fixed PNG schema.");
            }

            if (!IsMetadataDescriptor(metadata, id))
            {
                throw new ArgumentException("Metadata descriptor must match the fixed sidecar schema.");
            }

            return new PngJsonCapturePublicationPlanEntry(
                evidence.CaptureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                image.ByteLength,
                metadata.ByteLength,
                image.ContentHash,
                metadata.ContentHash);
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
