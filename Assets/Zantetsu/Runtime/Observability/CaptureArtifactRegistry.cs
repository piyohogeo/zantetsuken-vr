using System;

namespace Zantetsu.Observability
{
    /// <summary>Fixed-capacity append-only registry of staged generic artifacts.</summary>
    internal sealed class CaptureArtifactRegistry
    {
        private readonly CaptureArtifactDescriptor[] _descriptors;
        private readonly CaptureFrameWorkToken[] _tokens;
        private int _count;

        internal CaptureArtifactRegistry(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _descriptors = new CaptureArtifactDescriptor[capacity];
            _tokens = new CaptureFrameWorkToken[capacity];
        }

        internal int Capacity => _descriptors.Length;
        internal int Count => _count;

        internal bool TryRegister(in CaptureFrameWorkToken token, CaptureArtifactDescriptor descriptor)
        {
            if (!token.IsValid) throw new ArgumentException("Token must be valid.", nameof(token));
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            for (int i = 0; i < _count; i++)
            {
                if (string.Equals(_descriptors[i].ArtifactId, descriptor.ArtifactId, StringComparison.Ordinal)
                    || string.Equals(_descriptors[i].StagingRelativePath, descriptor.StagingRelativePath, StringComparison.Ordinal)
                    || string.Equals(_descriptors[i].FinalRelativePath, descriptor.FinalRelativePath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Artifact identity or path is already registered.");
                }
            }

            if (_count == _descriptors.Length) return false;
            _tokens[_count] = token;
            _descriptors[_count] = descriptor;
            _count++;
            return true;
        }

        internal CaptureArtifactDescriptor GetDescriptor(int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _descriptors[index];
        }

        internal CaptureFrameWorkToken GetWorkToken(int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _tokens[index];
        }

        internal int CountForFrame(long captureFrameId)
        {
            if (captureFrameId <= 0) throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            int count = 0;
            for (int i = 0; i < _count; i++) if (_tokens[i].CaptureFrameId == captureFrameId) count++;
            return count;
        }
    }
}
