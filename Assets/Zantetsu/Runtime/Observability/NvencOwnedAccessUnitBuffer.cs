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
    /// resource-resolution gate. Acquire, transfer, cancel, and return all
    /// fail without changing any field while the process is poisoned; an
    /// occupied region is then held, never guessed back to Free. The copy
    /// claims copy-in-progress inside the gate, runs the source outside the
    /// gate, and commits the valid length inside a second gate; a transient
    /// commit-gate contention parks the length and a poisoned commit is
    /// refused, so the source is never contacted twice for one lease and
    /// content never becomes ready after poison.
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
        /// <summary>
        /// Outcome of one collector-side copy attempt.
        /// </summary>
        internal enum NvencAccessUnitCopyStatus
        {
            /// <summary>The valid length is recorded and the content is ready.</summary>
            Committed,

            /// <summary>The copy succeeded but the commit was deferred behind a
            /// transient gate; commit later without re-calling the source.</summary>
            Pending,

            /// <summary>The source reported no valid content; the controlled
            /// failure path releases the region and sample slot.</summary>
            Rejected,

            /// <summary>The source was not contacted: poison, gate contention,
            /// lease mismatch, already-ready, or copy-in-progress.</summary>
            NotStarted,
        }

        private readonly byte[] _storage;
        private readonly Guid _ownerToken;
        private readonly NvencCaptureProcessState _processState;

        private int _phase;
        private long _generation;
        private CaptureFrameWorkToken _workToken;
        private int _validLength;
        private bool _contentReady;
        private bool _retired;
        private bool _copyInProgress;
        private bool _copyPending;
        private int _pendingValidLength;

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
                _copyInProgress = false;
                _copyPending = false;
                _pendingValidLength = 0;
                writeLease = new NvencAccessUnitWriteLease(_ownerToken, _generation, workToken);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Collector-side copy. A short resource-resolution gate validates the
        /// exact write lease and claims copy-in-progress exactly once, so a
        /// concurrent second copy on the same lease is rejected before any side
        /// effect. The source call runs outside the gate; on return a second
        /// short gate commits the valid length. Poison ordered before the
        /// commit blocks content-ready and transfer; ordinary gate contention
        /// parks the valid length in <c>_copyPending</c> for a later
        /// <see cref="TryCommitPendingCopy"/> without re-calling the source.
        /// </summary>
        internal NvencAccessUnitCopyStatus TryCopyCompletedOutput(
            in NvencAccessUnitWriteLease writeLease,
            in NvencEncodeSampleSlotLease sampleSlot,
            INvencOutputBitstreamSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return NvencAccessUnitCopyStatus.NotStarted;
            }

            try
            {
                if (!IsExactCollector(writeLease))
                {
                    return NvencAccessUnitCopyStatus.NotStarted;
                }

                if (_contentReady || _copyInProgress || _copyPending)
                {
                    return NvencAccessUnitCopyStatus.NotStarted;
                }

                _copyInProgress = true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            // External call: completion wait, lock, copy, unlock, unmap.
            if (!source.TryCopyCompletedOutput(_workToken, sampleSlot, _storage, _storage.Length, out int validLength))
            {
                _copyInProgress = false;
                return NvencAccessUnitCopyStatus.Rejected;
            }

            if (validLength <= 0 || validLength > _storage.Length)
            {
                _copyInProgress = false;
                return NvencAccessUnitCopyStatus.Rejected;
            }

            // Commit the valid length behind a short gate.
            if (!_processState.TryBeginResourceResolution())
            {
                if (_processState.IsPoisoned)
                {
                    _copyInProgress = false;
                    return NvencAccessUnitCopyStatus.NotStarted;
                }

                _copyPending = true;
                _pendingValidLength = validLength;
                _copyInProgress = false;
                return NvencAccessUnitCopyStatus.Pending;
            }

            try
            {
                _validLength = validLength;
                _contentReady = true;
                _copyInProgress = false;
                return NvencAccessUnitCopyStatus.Committed;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Commits a copy parked by <see cref="TryCopyCompletedOutput"/> when
        /// the commit gate was contended. Returns <c>true</c> once the valid
        /// length is recorded and the content is ready, without re-calling the
        /// source; returns <c>false</c> while the gate is held or the process
        /// is poisoned.
        /// </summary>
        internal bool TryCommitPendingCopy(in NvencAccessUnitWriteLease writeLease)
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

                if (_copyPending)
                {
                    _validLength = _pendingValidLength;
                    _contentReady = true;
                    _copyPending = false;
                    _pendingValidLength = 0;
                    return true;
                }

                return _contentReady;
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
            _copyInProgress = false;
            _copyPending = false;
            _pendingValidLength = 0;
        }
    }
}
