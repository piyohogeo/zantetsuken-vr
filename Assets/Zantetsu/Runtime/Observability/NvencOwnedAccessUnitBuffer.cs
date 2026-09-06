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
    /// generation, the exact bound <see cref="CaptureFrameWorkToken"/>, the
    /// determined valid length, and the content-ready flag. The write and
    /// owned leases never receive the storage reference. Content is written
    /// only by the buffer authority synchronously calling the injected
    /// <see cref="INvencOutputBitstreamSource"/> with the fixed storage during
    /// CollectorOwned; a successful copy records the valid length exactly once,
    /// and only then can the region transfer to SinkOwned.
    /// </para>
    /// <para>
    /// Every transition is serialized against the Poison transition through
    /// the injected <see cref="NvencCaptureProcessState"/> short
    /// resource-resolution gate. Acquire, copy, transfer, cancel, and return
    /// all fail without changing any field while the process is poisoned; an
    /// occupied region is then held, never guessed back to Free.
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
        private bool _contentReady;
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
                _validLength = 0;
                _contentReady = false;
                writeLease = new NvencAccessUnitWriteLease(_ownerToken, _generation, workToken);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Collector-side copy: validates the exact write lease and work token,
        /// then synchronously calls the injected source with the fixed storage.
        /// A successful copy records the valid length exactly once and marks the
        /// content ready; a second copy in the same generation is rejected
        /// before any side effect. The completion wait, lock, copy, unlock, and
        /// unmap run outside the short process-state gate.
        /// </summary>
        internal bool TryCopyCompletedOutput(
            in NvencAccessUnitWriteLease writeLease,
            in NvencEncodeSampleSlotLease sampleSlot,
            INvencOutputBitstreamSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            // Pre-validation and claim of the single copy (short gate).
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

                if (_contentReady)
                {
                    return false;
                }
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            // External call: completion wait, lock, copy, unlock, unmap.
            if (!source.TryCopyCompletedOutput(_workToken, sampleSlot, _storage, _storage.Length, out int validLength))
            {
                return false;
            }

            if (validLength <= 0 || validLength > _storage.Length)
            {
                return false;
            }

            // Record the success exactly once, serialized against poison.
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

                if (_contentReady)
                {
                    return false;
                }

                _validLength = validLength;
                _contentReady = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Moves the region from CollectorOwned to SinkOwned using only the
        /// recorded valid length. Fails while no content has been copied for
        /// the current generation; the recorded length is retained by the
        /// buffer authority for the deferred sink consume.
        /// </summary>
        internal bool TryTransferToSink(
            in NvencAccessUnitWriteLease writeLease,
            out NvencOwnedAccessUnitLease ownedLease)
        {
            ownedLease = default;

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

                if (!_contentReady)
                {
                    return false;
                }

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
            _contentReady = false;
        }
    }
}
