using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity logical pool of eight Submit-to-Output capacity credits
    /// for the Phase 0.11 NVENC path. It reserves and releases credits only; it
    /// owns no queue, buffer, surface, handle, or other native resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capacity is fixed at
    /// <see cref="NvencBringUpProfileV1.SubmitToOutputQueueCapacity"/> (always
    /// eight) and every backing array is allocated once in the constructor.
    /// <see cref="TryRent"/> and <see cref="TryReturn"/> perform no managed
    /// allocation, no waiting, and no I/O, and a full pool fails immediately
    /// with a <c>default</c> lease instead of waiting.
    /// </para>
    /// <para>
    /// <see cref="TryRent"/> succeeds only while the injected
    /// <see cref="NvencCaptureProcessState"/> is accepting (Running). Draining
    /// and poison reject new reservations. Returning an active lease is allowed
    /// while Running or Draining, but after poison every return is refused so
    /// the credit stays occupied until the process exits. There is no reset,
    /// clear, force-return, or resize API.
    /// </para>
    /// <para>
    /// The pool is used inside a single admission linearization boundary and is
    /// not internally synchronized; its future return side is serialized by the
    /// resource-resolution gate.
    /// </para>
    /// </remarks>
    internal sealed class NvencSubmitToOutputCreditPool
    {
        private readonly Guid _ownerToken;
        private readonly NvencCaptureProcessState _processState;
        private readonly long[] _generations;
        private readonly bool[] _rented;
        private readonly bool[] _retired;

        private int _rentedCount;

        internal NvencSubmitToOutputCreditPool(NvencCaptureProcessState processState)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            _ownerToken = Guid.NewGuid();
            _processState = processState;

            _generations = new long[NvencBringUpProfileV1.SubmitToOutputQueueCapacity];
            for (int i = 0; i < _generations.Length; i++)
            {
                _generations[i] = 1;
            }

            _rented = new bool[NvencBringUpProfileV1.SubmitToOutputQueueCapacity];
            _retired = new bool[NvencBringUpProfileV1.SubmitToOutputQueueCapacity];
            _rentedCount = 0;
        }

        internal int Capacity => NvencBringUpProfileV1.SubmitToOutputQueueCapacity;

        internal int OccupiedCount => _rentedCount;

        internal bool TryRent(out NvencSubmitToOutputCreditLease lease)
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
                    lease = new NvencSubmitToOutputCreditLease(_ownerToken, i, _generations[i]);
                    return true;
                }
            }

            lease = default;
            return false;
        }

        internal bool TryReturn(in NvencSubmitToOutputCreditLease lease)
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
                _retired[index] = true;
            }
            else
            {
                _generations[index]++;
            }

            return true;
        }

        internal bool IsActive(in NvencSubmitToOutputCreditLease lease)
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
