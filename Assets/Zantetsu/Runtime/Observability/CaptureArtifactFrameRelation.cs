using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Format-independent relation between one artifact and zero or more
    /// capture frames. Empty means run-scoped; multiple IDs allow one artifact
    /// to be referenced by more than one frame. This is independent of the
    /// work token that happened to produce the artifact.
    /// </summary>
    internal sealed class CaptureArtifactFrameRelation
    {
        private readonly long[] _captureFrameIds;

        internal CaptureArtifactFrameRelation(long[] captureFrameIds)
        {
            if (captureFrameIds == null) throw new ArgumentNullException(nameof(captureFrameIds));
            long previous = 0;
            for (int i = 0; i < captureFrameIds.Length; i++)
            {
                long id = captureFrameIds[i];
                if (id <= 0 || (i > 0 && id <= previous))
                    throw new ArgumentException("Frame IDs must be positive, unique, and strictly ascending.", nameof(captureFrameIds));
                previous = id;
            }
            _captureFrameIds = new long[captureFrameIds.Length];
            Array.Copy(captureFrameIds, _captureFrameIds, captureFrameIds.Length);
        }

        internal int Count => _captureFrameIds.Length;
        internal long GetCaptureFrameId(int index)
        {
            if (index < 0 || index >= _captureFrameIds.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _captureFrameIds[index];
        }

        internal bool Contains(long captureFrameId) => Array.BinarySearch(_captureFrameIds, captureFrameId) >= 0;

        internal bool IsValid
        {
            get
            {
                if (_captureFrameIds == null) return false;
                long previous = 0;
                for (int i = 0; i < _captureFrameIds.Length; i++)
                {
                    long id = _captureFrameIds[i];
                    if (id <= 0 || (i > 0 && id <= previous)) return false;
                    previous = id;
                }
                return true;
            }
        }
    }
}
