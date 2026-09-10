using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The one predicate that decides whether a recovered canonical publication
    /// plan is the fixed Phase 0.11 NVENC graph for a given Run.
    /// </summary>
    /// <remarks>
    /// The generic structural rules - descriptor validity, ascending artifact
    /// ids, and frame-to-artifact agreement - are already enforced by
    /// <see cref="CapturePublicationPlan.IsValid"/> and are reused rather than
    /// restated. What this predicate adds is only what
    /// <see cref="NvencRunPublicationPlanBuilder"/> fixes for Phase 0.11: one
    /// artifact, the fixed
    /// <see cref="NvencRunChunkArtifactDescriptorFactory"/> format, version, and
    /// staging and final relative paths, a positive declared length, a
    /// lowercase hex SHA-256 content hash, and a frame relation of one to
    /// <see cref="NvencBringUpProfileV1.CadenceTickCount"/> frames that all name
    /// exactly that artifact. It performs no filesystem, codec, or hash work,
    /// holds no state, and never throws.
    /// </remarks>
    internal static class NvencRunPublicationRecoveryPlanShape
    {
        internal static bool IsFixedPhase011Plan(
            CapturePublicationPlan plan,
            long testRunId,
            string runInitializationId)
        {
            try
            {
                if (plan == null || !plan.IsValid)
                {
                    return false;
                }

                if (plan.TestRunId != testRunId
                    || !string.Equals(plan.RunInitializationId, runInitializationId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (plan.ArtifactCount != 1)
                {
                    return false;
                }

                CaptureArtifactDescriptor descriptor = plan.GetArtifact(0);
                if (!IsFixedChunkDescriptor(descriptor))
                {
                    return false;
                }

                int frameCount = plan.CaptureFrameEvidenceCount;
                if (frameCount < 1 || frameCount > NvencBringUpProfileV1.CadenceTickCount)
                {
                    return false;
                }

                for (int i = 0; i < frameCount; i++)
                {
                    CaptureFrameEvidenceEntry entry = plan.GetCaptureFrameEvidence(i);
                    if (entry == null || !entry.IsValid || entry.ArtifactCount != 1)
                    {
                        return false;
                    }

                    if (!string.Equals(
                            entry.GetArtifactId(0), descriptor.ArtifactId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsFixedChunkDescriptor(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid)
            {
                return false;
            }

            if (descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
            {
                return false;
            }

            if (!string.Equals(
                    descriptor.FormatId,
                    NvencRunChunkArtifactDescriptorFactory.FormatId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (descriptor.FormatVersion != NvencRunChunkArtifactDescriptorFactory.FormatVersion)
            {
                return false;
            }

            if (!string.Equals(
                    descriptor.StagingRelativePath,
                    NvencRunChunkArtifactDescriptorFactory.StagingRelativePath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                    descriptor.FinalRelativePath,
                    NvencRunChunkArtifactDescriptorFactory.FinalRelativePath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return descriptor.ByteLength > 0 && IsLowerHex(descriptor.ContentHash, 64);
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                if (!digit && !lower)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
