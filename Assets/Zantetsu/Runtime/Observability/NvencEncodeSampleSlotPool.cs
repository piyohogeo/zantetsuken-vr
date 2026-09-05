using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity logical pool of eight NVENC Encode Sample Slots for the
    /// Phase 0.11 NVENC capture path. It reserves and releases slots only; it
    /// owns no Texture, NVENC handle, Completion Event, queue, or other native
    /// resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capacity is fixed at
    /// <see cref="NvencBringUpProfileV1.EncodeSampleSlotCount"/> (always eight)
    /// and every backing array is allocated once in the constructor.
    /// <see cref="TryRent"/> and <see cref="TryReturn"/> perform no managed
    /// allocation, no waiting, and no I/O, and a full pool fails immediately
    /// with a <c>default</c> lease instead of waiting.
    /// </para>
    /// <para>
    /// <see cref="TryRent"/> succeeds only while the injected
    /// <see cref="NvencCaptureProcessState"/> is accepting (Running). Draining
    /// and poison reject new reservations. Returning an active lease is allowed
    /// while Running or Draining, but after poison every return is refused so
    /// the slot stays occupied until the process exits. This pool offers no
    /// unpoison, reset, clear, force-return, or resize API and never reclaims
    /// slots on its own.
    /// </para>
    /// <para>
    /// The pool is intended for a single admission linearization boundary and
    /// is not internally synchronized.
    /// </para>
    /// </remarks>
    internal sealed class NvencEncodeSampleSlotPool
    {
        private readonly Guid _ownerToken;
        private readonly NvencCaptureProcessState _processState;
        private readonly long[] _generations;
        private readonly bool[] _rented;
        private readonly bool[] _retired;

        private int _rentedCount;

        internal NvencEncodeSampleSlotPool(NvencCaptureProcessState processState)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            _ownerToken = Guid.NewGuid();
            _processState = processState;
            _generations = new long[NvencBringUpProfileV1.EncodeSampleSlotCount];
            _rented = new bool[NvencBringUpProfileV1.EncodeSampleSlotCount];
            _retired = new bool[NvencBringUpProfileV1.EncodeSampleSlotCount];
            _rentedCount = 0;
        }

        internal int Capacity => NvencBringUpProfileV1.EncodeSampleSlotCount;

        internal int OccupiedCount => _rentedCount;

        internal bool TryRent(out NvencEncodeSampleSlotLease lease)
        {
            if (!_processState.IsAccepting)
            {
                lease = default;
                return false;
            }

            for (int i = 0; i < _rented.Length; i++)
            {
                if (!_rented[i] && !_retired[i])
                {
                    _rented[i] = true;
                    _rentedCount++;
                    lease = new NvencEncodeSampleSlotLease(_ownerToken, i, _generations[i]);
                    return true;
                }
            }

            lease = default;
            return false;
        }

        internal bool TryReturn(in NvencEncodeSampleSlotLease lease)
        {
            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!lease.IsValid || lease.OwnerToken != _ownerToken)
            {
                return false;
            }

            int index = lease.SlotIndex;
            if (index < 0 || index >= _rented.Length)
            {
                return false;
            }

            if (lease.Generation != _generations[index] || !_rented[index])
            {
                return false;
            }

            _rented[index] = false;
            _rentedCount--;

            if (_generations[index] == long.MaxValue)
            {
                // Advancing would wrap. Retire the slot so it is never rented
                // again; the current reservation is still released.
                _retired[index] = true;
            }
            else
            {
                _generations[index]++;
            }

            return true;
        }

        internal bool IsActive(in NvencEncodeSampleSlotLease lease)
        {
            if (!lease.IsValid || lease.OwnerToken != _ownerToken)
            {
                return false;
            }

            int index = lease.SlotIndex;
            if (index < 0 || index >= _rented.Length)
            {
                return false;
            }

            return _rented[index] && lease.Generation == _generations[index];
        }
    }
}
