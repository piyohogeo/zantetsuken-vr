using System;

namespace Zantetsu.Observability
{
    /// <summary>Immutable relation between one capture frame and zero or more artifacts.</summary>
    internal sealed class CaptureFrameEvidenceEntry
    {
        private readonly long _captureFrameId;
        private readonly string[] _artifactIds;

        internal CaptureFrameEvidenceEntry(long captureFrameId, string[] artifactIds)
        {
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            }

            if (artifactIds == null)
            {
                throw new ArgumentNullException(nameof(artifactIds));
            }

            string previous = null;
            for (int i = 0; i < artifactIds.Length; i++)
            {
                string id = artifactIds[i];
                if (string.IsNullOrEmpty(id))
                {
                    throw new ArgumentException("Artifact IDs must not be null or empty.", nameof(artifactIds));
                }

                if (previous != null && string.CompareOrdinal(previous, id) >= 0)
                {
                    throw new ArgumentException("Artifact IDs must be strictly ascending without duplicates.", nameof(artifactIds));
                }

                previous = id;
            }

            _captureFrameId = captureFrameId;
            _artifactIds = new string[artifactIds.Length];
            Array.Copy(artifactIds, _artifactIds, artifactIds.Length);
        }

        internal long CaptureFrameId => _captureFrameId;
        internal int ArtifactCount => _artifactIds.Length;

        internal string GetArtifactId(int index)
        {
            if (index < 0 || index >= _artifactIds.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _artifactIds[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_captureFrameId <= 0 || _artifactIds == null) return false;
                string previous = null;
                for (int i = 0; i < _artifactIds.Length; i++)
                {
                    string id = _artifactIds[i];
                    if (string.IsNullOrEmpty(id) || (previous != null && string.CompareOrdinal(previous, id) >= 0)) return false;
                    previous = id;
                }
                return true;
            }
        }
    }
}
