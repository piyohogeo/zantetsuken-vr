using System;
using System.Collections.Generic;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical format-independent publication expectation. Artifact
    /// descriptors are the authority; frame entries only express relations.
    /// </summary>
    internal sealed class CapturePublicationPlan
    {
        private readonly long _testRunId;
        private readonly string _runInitializationId;
        private readonly string _runManifestContentHash;
        private readonly CaptureArtifactDescriptor[] _artifactDescriptors;
        private readonly CaptureFrameEvidenceEntry[] _captureFrameEvidenceEntries;

        internal CapturePublicationPlan(
            long testRunId,
            string runInitializationId,
            string runManifestContentHash,
            CaptureArtifactDescriptor[] artifactDescriptors,
            CaptureFrameEvidenceEntry[] captureFrameEvidenceEntries)
        {
            if (testRunId <= 0) throw new ArgumentOutOfRangeException(nameof(testRunId));
            if (!IsLowerHex(runInitializationId, 32)) throw new ArgumentException("Initialization ID must be 32 lowercase hex characters.", nameof(runInitializationId));
            if (!IsLowerHex(runManifestContentHash, 64)) throw new ArgumentException("Manifest hash must be 64 lowercase hex characters.", nameof(runManifestContentHash));
            if (artifactDescriptors == null) throw new ArgumentNullException(nameof(artifactDescriptors));
            if (captureFrameEvidenceEntries == null) throw new ArgumentNullException(nameof(captureFrameEvidenceEntries));
            if (artifactDescriptors.Length > 200000) throw new ArgumentOutOfRangeException(nameof(artifactDescriptors));
            if (captureFrameEvidenceEntries.Length > 100000) throw new ArgumentOutOfRangeException(nameof(captureFrameEvidenceEntries));

            ValidateDescriptors(artifactDescriptors);
            ValidateEvidence(captureFrameEvidenceEntries, artifactDescriptors);

            _testRunId = testRunId;
            _runInitializationId = runInitializationId;
            _runManifestContentHash = runManifestContentHash;
            _artifactDescriptors = new CaptureArtifactDescriptor[artifactDescriptors.Length];
            _captureFrameEvidenceEntries = new CaptureFrameEvidenceEntry[captureFrameEvidenceEntries.Length];
            Array.Copy(artifactDescriptors, _artifactDescriptors, artifactDescriptors.Length);
            Array.Copy(captureFrameEvidenceEntries, _captureFrameEvidenceEntries, captureFrameEvidenceEntries.Length);
        }

        internal int SchemaVersion => 2;
        internal long TestRunId => _testRunId;
        internal string RunInitializationId => _runInitializationId;
        internal string RunManifestContentHash => _runManifestContentHash;
        internal int ArtifactCount => _artifactDescriptors.Length;
        internal int CaptureFrameEvidenceCount => _captureFrameEvidenceEntries.Length;

        internal CaptureArtifactDescriptor GetArtifact(int index)
        {
            if (index < 0 || index >= _artifactDescriptors.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _artifactDescriptors[index];
        }

        internal CaptureFrameEvidenceEntry GetCaptureFrameEvidence(int index)
        {
            if (index < 0 || index >= _captureFrameEvidenceEntries.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _captureFrameEvidenceEntries[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_testRunId <= 0 || !IsLowerHex(_runInitializationId, 32) || !IsLowerHex(_runManifestContentHash, 64)
                    || _artifactDescriptors == null || _captureFrameEvidenceEntries == null
                    || _artifactDescriptors.Length > 200000 || _captureFrameEvidenceEntries.Length > 100000)
                {
                    return false;
                }

                try
                {
                    ValidateDescriptors(_artifactDescriptors);
                    ValidateEvidence(_captureFrameEvidenceEntries, _artifactDescriptors);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        private static void ValidateDescriptors(CaptureArtifactDescriptor[] descriptors)
        {
            string previousId = null;
            HashSet<string> stagingPaths = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> finalPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < descriptors.Length; i++)
            {
                CaptureArtifactDescriptor descriptor = descriptors[i];
                if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptors must be valid.", nameof(descriptors));
                if (previousId != null && string.CompareOrdinal(previousId, descriptor.ArtifactId) >= 0) throw new ArgumentException("Descriptors must be ordered by unique artifact ID.", nameof(descriptors));

                if (!stagingPaths.Add(descriptor.StagingRelativePath)
                    || !finalPaths.Add(descriptor.FinalRelativePath))
                {
                    throw new ArgumentException("Artifact paths must be unique.", nameof(descriptors));
                }

                previousId = descriptor.ArtifactId;
            }
        }

        private static void ValidateEvidence(CaptureFrameEvidenceEntry[] entries, CaptureArtifactDescriptor[] descriptors)
        {
            long previousFrameId = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                CaptureFrameEvidenceEntry entry = entries[i];
                if (entry == null || !entry.IsValid || (i > 0 && entry.CaptureFrameId <= previousFrameId))
                {
                    throw new ArgumentException("Frame evidence must be valid and strictly ordered.", nameof(entries));
                }

                for (int j = 0; j < entry.ArtifactCount; j++)
                {
                    if (!ContainsArtifact(descriptors, entry.GetArtifactId(j)))
                    {
                        throw new ArgumentException("Frame evidence references an unknown artifact.", nameof(entries));
                    }
                }

                previousFrameId = entry.CaptureFrameId;
            }
        }

        private static bool ContainsArtifact(CaptureArtifactDescriptor[] descriptors, string id)
        {
            int lo = 0;
            int hi = descriptors.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                int compare = string.CompareOrdinal(descriptors[mid].ArtifactId, id);
                if (compare == 0) return true;
                if (compare < 0) lo = mid + 1; else hi = mid - 1;
            }
            return false;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
