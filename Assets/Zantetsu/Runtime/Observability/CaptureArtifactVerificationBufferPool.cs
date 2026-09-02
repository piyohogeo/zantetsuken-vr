using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-size, single-slot verification buffer pool. Exactly one buffer is
    /// available; <see cref="TryRent"/> returns it once per outstanding rent
    /// and <c>null</c> when exhausted, and <see cref="Return"/> releases it
    /// exactly once. It never allocates a buffer proportional to artifact
    /// length and never falls back to a larger allocation.
    /// </summary>
    internal sealed class CaptureArtifactVerificationBufferPool
    {
        private readonly byte[] _buffer;
        private int _outstanding;

        internal CaptureArtifactVerificationBufferPool(int bufferLength)
        {
            if (bufferLength < 1) throw new ArgumentOutOfRangeException(nameof(bufferLength));
            _buffer = new byte[bufferLength];
        }

        internal int BufferLength => _buffer.Length;

        internal int OutstandingRentCount => _outstanding;

        internal byte[] TryRent()
        {
            if (_outstanding != 0) return null;
            _outstanding = 1;
            return _buffer;
        }

        internal void Return(byte[] buffer)
        {
            if (buffer == null) return;
            if (ReferenceEquals(buffer, _buffer) && _outstanding != 0)
            {
                _outstanding = 0;
            }
        }
    }
}
