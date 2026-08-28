using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, strictly increasing set of capture frame IDs that remained
    /// pending at the freeze deadline and were forcibly dropped with reason 9.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IDs are positive, strictly increasing, alias-free, and stored in entry
    /// store order (therefore ascending). An empty set is valid.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> is derived from the held values; no independent
    /// validity flag is stored. The backing array is never returned; only
    /// <see cref="GetCaptureFrameId"/> and <see cref="Contains"/> expose it.
    /// </para>
    /// <para>
    /// The only canonical issuer is
    /// <see cref="CaptureFrameDraftRegistry.ForceDropPendingForFreeze"/>, which
    /// builds a fresh array once and passes it here; the registry never retains
    /// or publishes that array afterward, so it cannot be externally modified.
    /// The internal constructor is not a public issue path and must only be
    /// used by that registry (or by tests exercising the constructor contract).
    /// </para>
    /// <para>
    /// It owns and disposes no registry, draft, queue, or snapshot, holds no
    /// logger, native container, or mutable collection, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class ForcedDropFrameIdSet
    {
        private readonly CaptureFrameDraftRegistry _issuedBy;
        private readonly long _testRunId;
        private readonly long[] _captureFrameIds;
        private readonly int _count;

        internal ForcedDropFrameIdSet(
            CaptureFrameDraftRegistry issuedBy,
            long testRunId,
            long[] captureFrameIds)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (captureFrameIds == null)
            {
                throw new ArgumentNullException(nameof(captureFrameIds));
            }

            for (int i = 0; i < captureFrameIds.Length; i++)
            {
                if (captureFrameIds[i] <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(captureFrameIds), captureFrameIds[i], "Capture frame IDs must be positive.");
                }

                if (i > 0 && captureFrameIds[i] <= captureFrameIds[i - 1])
                {
                    throw new ArgumentException("Capture frame IDs must be strictly increasing.", nameof(captureFrameIds));
                }
            }

            _issuedBy = issuedBy;
            _testRunId = testRunId;
            _captureFrameIds = captureFrameIds;
            _count = captureFrameIds.Length;
        }

        public long TestRunId => _testRunId;

        public int Count => _count;

        internal CaptureFrameDraftRegistry IssuedBy => _issuedBy;

        public long GetCaptureFrameId(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the set.");
            }

            return _captureFrameIds[index];
        }

        public bool Contains(long captureFrameId)
        {
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            for (int i = 0; i < _count; i++)
            {
                if (_captureFrameIds[i] == captureFrameId)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsValid
        {
            get
            {
                if (_issuedBy == null)
                {
                    return false;
                }

                if (_testRunId <= 0)
                {
                    return false;
                }

                if (_captureFrameIds == null)
                {
                    return false;
                }

                if (_count != _captureFrameIds.Length)
                {
                    return false;
                }

                for (int i = 0; i < _count; i++)
                {
                    if (_captureFrameIds[i] <= 0)
                    {
                        return false;
                    }

                    if (i > 0 && _captureFrameIds[i] <= _captureFrameIds[i - 1])
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
