using System;
using System.Collections.Generic;

namespace Zantetsu.Observability
{
    /// <summary>Builds the generic publication authority from frozen staged drafts and artifacts.</summary>
    internal static class CapturePublicationPlanBuilder
    {
        internal static CapturePublicationPlan Build(
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts,
            string runInitializationId,
            string runManifestContentHash)
        {
            if (drafts == null) throw new ArgumentNullException(nameof(drafts));
            if (artifacts == null) throw new ArgumentNullException(nameof(artifacts));

            int stagedCount = 0;
            for (int i = 0; i < drafts.EntryCount; i++)
            {
                if (drafts.GetEntryStatus(i) == CaptureFrameDraftStatus.Staged) stagedCount++;
                else if (drafts.GetEntryStatus(i) == CaptureFrameDraftStatus.Pending) throw new InvalidOperationException("Publication cannot include pending drafts.");
            }

            long[] stagedIds = new long[stagedCount];
            int stagedIndex = 0;
            for (int i = 0; i < drafts.EntryCount; i++)
            {
                if (drafts.GetEntryStatus(i) == CaptureFrameDraftStatus.Staged)
                {
                    stagedIds[stagedIndex++] = drafts.GetEntryDraft(i).CaptureFrameId;
                }
            }
            Array.Sort(stagedIds);

            int descriptorCount = 0;
            for (int i = 0; i < artifacts.Count; i++)
                if (RelationCanPublish(artifacts.GetFrameRelation(i), stagedIds)) descriptorCount++;

            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[descriptorCount];
            int descriptorIndex = 0;
            for (int i = 0; i < artifacts.Count; i++)
                if (RelationCanPublish(artifacts.GetFrameRelation(i), stagedIds))
                    descriptors[descriptorIndex++] = artifacts.GetDescriptor(i);
            Array.Sort(descriptors, ArtifactComparer.Instance);

            CaptureFrameEvidenceEntry[] evidence = new CaptureFrameEvidenceEntry[stagedIds.Length];
            for (int i = 0; i < stagedIds.Length; i++)
            {
                long frameId = stagedIds[i];
                int count = 0;
                for (int j = 0; j < artifacts.Count; j++)
                    if (RelationCanPublish(artifacts.GetFrameRelation(j), stagedIds)
                        && artifacts.GetFrameRelation(j).Contains(frameId)) count++;
                string[] ids = new string[count];
                int index = 0;
                for (int j = 0; j < artifacts.Count; j++)
                    if (RelationCanPublish(artifacts.GetFrameRelation(j), stagedIds)
                        && artifacts.GetFrameRelation(j).Contains(frameId))
                        ids[index++] = artifacts.GetDescriptor(j).ArtifactId;
                Array.Sort(ids, StringComparer.Ordinal);
                evidence[i] = new CaptureFrameEvidenceEntry(frameId, ids);
            }

            return new CapturePublicationPlan(
                drafts.Run.TestRunId,
                runInitializationId,
                runManifestContentHash,
                descriptors,
                evidence);
        }

        private static bool RelationCanPublish(CaptureArtifactFrameRelation relation, long[] stagedIds)
        {
            if (relation == null || !relation.IsValid) return false;
            for (int i = 0; i < relation.Count; i++)
                if (Array.BinarySearch(stagedIds, relation.GetCaptureFrameId(i)) < 0) return false;
            return true;
        }

        private sealed class ArtifactComparer : IComparer<CaptureArtifactDescriptor>
        {
            internal static readonly ArtifactComparer Instance = new ArtifactComparer();
            public int Compare(CaptureArtifactDescriptor x, CaptureArtifactDescriptor y) =>
                string.CompareOrdinal(x.ArtifactId, y.ArtifactId);
        }
    }
}
