using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Issues monotonically increasing capture frame IDs. Main thread only;
    /// not thread-safe and performs no allocation, logging, or string
    /// formatting on the hot path.
    /// </summary>
    public sealed class CaptureFrameIdSequence
    {
        private long _lastIssued;

        public CaptureFrameIdSequence()
        {
            _lastIssued = 0;
        }

        internal CaptureFrameIdSequence(long lastIssued)
        {
            _lastIssued = lastIssued;
        }

        /// <summary>The most recently issued ID, or 0 before any issuance.</summary>
        public long LastIssued => _lastIssued;

        /// <summary>
        /// Issues the next ID, starting at 1 and increasing monotonically.
        /// Throws <see cref="OverflowException"/> once <see cref="long.MaxValue"/>
        /// has been issued.
        /// </summary>
        public long Next()
        {
            if (_lastIssued == long.MaxValue)
            {
                throw new OverflowException("Capture frame ID sequence has been exhausted.");
            }

            _lastIssued++;
            return _lastIssued;
        }
    }
}
