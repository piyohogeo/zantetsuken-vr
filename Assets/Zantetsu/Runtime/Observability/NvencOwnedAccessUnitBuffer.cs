using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The single fixed Access Unit region for the Phase 0.11 NVENC path
    /// (D-147). It allocates exactly one <c>byte[]</c> of
    /// <see cref="NvencBringUpProfileV1.MaxAccessUnitByteLength"/> in its
    /// constructor and never reallocates, resizes, or replaces the backing
    /// storage; the single fixed region is reused across generations. Ownership
    /// of the region moves one way Free → CollectorOwned → SinkOwned → Free,
    /// and at most one Access Unit is held at any time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer authority keeps the backing storage, the current phase, the
    /// generation, the exact bound <see cref="CaptureFrameWorkToken"/>, and the
    /// determined valid length. The write and owned leases never receive the
    /// storage reference; the collector writes through a bounded copy and the
    /// sink reads through a synchronous consume, each a one-shot operation
    /// validated against the exact lease by the buffer authority.
    /// </para>
    /// <para>
    /// Every transition is serialized against the Poison transition through
    /// the injected <see cref="NvencCaptureProcessState"/> short
    /// resource-resolution gate. Acquire, transfer, cancel, return, copy, and
    /// consume all fail without changing any field while the process
    /// is poisoned; an occupied region is then held, never guessed back to
    /// Free.
    /// </para>
    /// <para>
    /// A controlled failure releases the region exactly once: the collector
    /// cancels the write lease and the sink returns the owned lease. Each
    /// successful release advances the generation; at the generation ceiling
    /// the single slot is retired instead of wrapping, so no ABA reuse is
    /// possible. This type owns no OS or native resource and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencOwnedAccessUnitBuffer
    {
        private readonly byte[] _storage;
        private readonly Guid _ownerToken;
        private readonly NvencCaptureProcessState _processState;

        private int _phase;
        private long _generation;
        private CaptureFrameWorkToken _workToken;
        private int _validLength;
        private bool _retired;

        internal NvencOwnedAccessUnitBuffer(NvencCaptureProcessState processState)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            _storage = new byte[(int)NvencBringUpProfileV1.MaxAccessUnitByteLength];
            _ownerToken = Guid.NewGuid();
            _processState = processState;
            _phase = (int)NvencAccessUnitPhase.Free;
            _generation = 1;
            _workToken = default;
            _validLength = 0;
            _retired = false;
        }

        /// <summary>
        /// The fixed region length in bytes, always equal to
        /// <see cref="NvencBringUpProfileV1.MaxAccessUnitByteLength"/>.
        /// </summary>
        internal int Capacity => _storage.Length;

        /// <summary>
        /// The current ownership phase of the single region.
        /// </summary>
        internal NvencAccessUnitPhase Phase => (NvencAccessUnitPhase)_phase;

        /// <summary>
        /// Reserves the region for the collector and binds the exact work token
        /// and current generation. Succeeds only from Free, while the process
        /// is Running or Draining; poisoned, retired, or already-occupied
        /// states fail without changing any field.
        /// </summary>
        internal bool TryBeginWrite(in CaptureFrameWorkToken workToken, out NvencAccessUnitWriteLease writeLease)
        {
            writeLease = default;

            if (!workToken.IsValid)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_retired || _phase != (int)NvencAccessUnitPhase.Free)
                {
                    return false;
                }

                _phase = (int)NvencAccessUnitPhase.CollectorOwned;
                _workToken = workToken;
                writeLease = new NvencAccessUnitWriteLease(_ownerToken, _generation, workToken);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Collector-side bounded copy: writes <paramref name="count"/> bytes
        /// from <paramref name="source"/> into the region after validating the
        /// exact write lease, its generation and work token, and the
        /// CollectorOwned phase. The region never escapes to the caller, so a
        /// held lease cannot mutate the region after transfer, return, or
        /// poison. Foreign, stale, double, wrong-phase, or out-of-range
        /// leases and bounds are rejected without changing the region content.
        /// </summary>
        internal bool TryCopyCollectorContent(
            in NvencAccessUnitWriteLease writeLease,
            byte[] source,
            int sourceOffset,
            int count)
        {
            if (source == null || sourceOffset < 0 || count <= 0)
            {
                return false;
            }

            if (count > _storage.Length)
            {
                return false;
            }

            if (sourceOffset > source.Length || count > source.Length - sourceOffset)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactCollector(writeLease))
                {
                    return false;
                }

                Buffer.BlockCopy(source, sourceOffset, _storage, 0, count);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Moves the region from CollectorOwned to SinkOwned and determines the
        /// valid length exactly once. The valid length must be in
        /// 1..MaxAccessUnitByteLength; on any failure the phase, length, and
        /// generation are left unchanged.
        /// </summary>
        internal bool TryTransferToSink(
            in NvencAccessUnitWriteLease writeLease,
            int validLength,
            out NvencOwnedAccessUnitLease ownedLease)
        {
            ownedLease = default;

            if (validLength <= 0 || validLength > _storage.Length)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactCollector(writeLease))
                {
                    return false;
                }

                _validLength = validLength;
                _phase = (int)NvencAccessUnitPhase.SinkOwned;
                ownedLease = new NvencOwnedAccessUnitLease(_ownerToken, _generation, _workToken);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Sink-side synchronous consume: copies the determined valid length
        /// from the region into <paramref name="destination"/> after validating
        /// the exact owned lease, its generation and work token, and the
        /// SinkOwned phase. The region never escapes to the caller, so a held
        /// lease cannot read or mutate a later generation after return.
        /// Foreign, stale, double, wrong-phase, or undersized destinations are
        /// rejected without changing the region content.
        /// </summary>
        internal bool TryConsumeSinkContent(
            in NvencOwnedAccessUnitLease ownedLease,
            byte[] destination,
            int destinationOffset,
            out int consumedLength)
        {
            consumedLength = 0;

            if (destination == null || destinationOffset < 0)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactSink(ownedLease))
                {
                    return false;
                }

                int length = _validLength;
                if (destinationOffset > destination.Length || length > destination.Length - destinationOffset)
                {
                    return false;
                }

                Buffer.BlockCopy(_storage, 0, destination, destinationOffset, length);
                consumedLength = length;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Collector-side controlled failure: releases an exact CollectorOwned
        /// write lease back to Free exactly once and advances the generation.
        /// Foreign, stale, double, or wrong-phase leases are rejected without
        /// changing any field.
        /// </summary>
        internal bool CancelWrite(in NvencAccessUnitWriteLease writeLease)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactCollector(writeLease))
                {
                    return false;
                }

                ReleaseToFree();
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Sink-side success or controlled failure: releases an exact SinkOwned
        /// owned lease back to Free exactly once and advances the generation.
        /// Foreign, stale, double, or wrong-phase leases are rejected without
        /// changing any field.
        /// </summary>
        internal bool Return(in NvencOwnedAccessUnitLease ownedLease)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactSink(ownedLease))
                {
                    return false;
                }

                ReleaseToFree();
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool IsExactCollector(in NvencAccessUnitWriteLease writeLease)
        {
            return writeLease.IsValid &&
                writeLease.OwnerToken == _ownerToken &&
                writeLease.Generation == _generation &&
                _phase == (int)NvencAccessUnitPhase.CollectorOwned &&
                writeLease.WorkToken.IdenticalTo(_workToken);
        }

        private bool IsExactSink(in NvencOwnedAccessUnitLease ownedLease)
        {
            return ownedLease.IsValid &&
                ownedLease.OwnerToken == _ownerToken &&
                ownedLease.Generation == _generation &&
                _phase == (int)NvencAccessUnitPhase.SinkOwned &&
                ownedLease.WorkToken.IdenticalTo(_workToken);
        }

        private void ReleaseToFree()
        {
            if (_generation == long.MaxValue)
            {
                _retired = true;
            }
            else
            {
                _generation++;
            }

            _phase = (int)NvencAccessUnitPhase.Free;
            _workToken = default;
            _validLength = 0;
        }
    }
}
