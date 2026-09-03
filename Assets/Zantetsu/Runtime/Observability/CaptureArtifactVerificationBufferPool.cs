using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-size, single-slot verification buffer pool. A short lock guards
    /// rent and return, and every successful rent mints a one-time
    /// <see cref="Lease"/> carrying a monotonic generation, so concurrent rents
    /// can never share the buffer and a stale return of an already-returned
    /// lease can never release a later borrow. It never allocates a buffer
    /// proportional to artifact length and never falls back to a larger
    /// allocation.
    /// </summary>
    internal sealed class CaptureArtifactVerificationBufferPool
    {
        private readonly byte[] _buffer;
        private readonly object _gate = new object();
        private bool _leased;
        private long _generation;

        internal CaptureArtifactVerificationBufferPool(int bufferLength)
        {
            if (bufferLength < 1) throw new ArgumentOutOfRangeException(nameof(bufferLength));
            _buffer = new byte[bufferLength];
        }

        internal int BufferLength => _buffer.Length;

        internal int OutstandingRentCount
        {
            get { lock (_gate) return _leased ? 1 : 0; }
        }

        internal Lease TryRent()
        {
            lock (_gate)
            {
                if (_leased) return null;
                _leased = true;
                _generation++;
                return new Lease(this, _buffer, _generation);
            }
        }

        internal void Return(Lease lease)
        {
            if (lease == null) return;
            lock (_gate)
            {
                if (!_leased || !ReferenceEquals(lease.Pool, this) || lease.Generation != _generation)
                {
                    return;
                }

                _leased = false;
            }
        }

        /// <summary>
        /// Reports whether a lease is the currently outstanding lease of this
        /// pool: it must have been minted by this pool and still carry the
        /// current generation. A foreign, returned, or stale lease is inactive.
        /// </summary>
        internal bool IsActive(Lease lease)
        {
            if (lease == null) return false;
            lock (_gate)
            {
                return _leased && ReferenceEquals(lease.Pool, this) && lease.Generation == _generation;
            }
        }

        /// <summary>One-time, generation-bound lease of the single buffer.</summary>
        internal sealed class Lease
        {
            private readonly CaptureArtifactVerificationBufferPool _pool;
            private readonly byte[] _buffer;
            private readonly long _generation;

            internal Lease(CaptureArtifactVerificationBufferPool pool, byte[] buffer, long generation)
            {
                _pool = pool;
                _buffer = buffer;
                _generation = generation;
            }

            internal CaptureArtifactVerificationBufferPool Pool => _pool;
            internal byte[] Buffer => _buffer;
            internal long Generation => _generation;
        }
    }
}
