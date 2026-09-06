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
    /// claims copy-in-progress inside the gate and holds it until the region
    /// is released back to Free; the source runs outside the gate and the
    /// valid length is committed only inside a second gate, so the source is
    /// never contacted twice for one lease and no buffer field changes after
    /// poison.
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

        /// <summary>
        /// Generation-bound proof that one source copy succeeded and its valid
        /// length is pending commit. Issued only by
        /// <see cref="TryCopyCompletedOutput"/> when it defers a commit, and
        /// bound to a per-generation secret held only by this buffer, so an
        /// equivalent proof cannot be reproduced from the lease or other
        /// constituent values.
        /// </summary>
        internal readonly struct NvencAccessUnitCopyProof
        {
            private readonly Guid _ownerToken;
            private readonly long _generation;
            private readonly int _validLength;
            private readonly Guid _nonce;

            internal NvencAccessUnitCopyProof(Guid ownerToken, long generation, int validLength, Guid nonce)
            {
                _ownerToken = ownerToken;
                _generation = generation;
                _validLength = validLength;
                _nonce = nonce;
            }

            internal bool Matches(Guid ownerToken, long generation, Guid nonce, int capacity, out int validLength)
            {
                validLength = _validLength;
                return _ownerToken == ownerToken &&
                    _generation == generation &&
                    _nonce == nonce &&
                    _validLength > 0 &&
                    _validLength <= capacity;
            }
        }

        /// <summary>
        /// Generation-bound proof that one sink consume ran and its outcome is
        /// pending finalization. Issued only by
        /// <see cref="TryConsumeSinkContent"/> when it defers the consumer
        /// return behind a transient gate, and bound to a per-generation secret
        /// held only by this buffer, so an equivalent proof cannot be
        /// reproduced from the lease or other constituent values.
        /// </summary>
        internal readonly struct NvencAccessUnitConsumeProof
        {
            private readonly Guid _ownerToken;
            private readonly long _generation;
            private readonly int _validLength;
            private readonly NvencRunChunkAppendOutcome _outcome;
            private readonly Guid _nonce;

            internal NvencAccessUnitConsumeProof(
                Guid ownerToken,
                long generation,
                int validLength,
                NvencRunChunkAppendOutcome outcome,
                Guid nonce)
            {
                _ownerToken = ownerToken;
                _generation = generation;
                _validLength = validLength;
                _outcome = outcome;
                _nonce = nonce;
            }

            internal bool Matches(
                Guid ownerToken,
                long generation,
                Guid nonce,
                out NvencRunChunkAppendOutcome outcome,
                out int validLength)
            {
                outcome = _outcome;
                validLength = _validLength;
                return _ownerToken == ownerToken &&
                    _generation == generation &&
                    _nonce == nonce &&
                    _validLength > 0 &&
                    (_outcome == NvencRunChunkAppendOutcome.Appended ||
                     _outcome == NvencRunChunkAppendOutcome.RejectedBeforeWrite);
            }
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
        private bool _copyInFlight;
        private Guid _copyNonce;
        private bool _consumeInFlight;
        private bool _contentConsumed;
        private Guid _consumeNonce;

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
            _copyNonce = Guid.NewGuid();
            _consumeNonce = Guid.NewGuid();
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
                _copyInFlight = false;
                _copyNonce = Guid.NewGuid();
                _consumeInFlight = false;
                _contentConsumed = false;
                _consumeNonce = Guid.NewGuid();
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
        /// exact write lease and claims copy-in-progress exactly once; the
        /// claim is held until the region is released back to Free, so a
        /// concurrent second copy on the same lease is rejected before any side
        /// effect even after a source rejection. The source call runs outside
        /// the gate; on return a second short gate commits the valid length.
        /// Poison or gate contention ordered before the commit returns Pending
        /// with a generation-bound proof, never writing a buffer field; the
        /// caller parks the proof and commits it later via
        /// <see cref="TryCommitCopiedContent"/> without re-calling the source.
        /// </summary>
        internal NvencAccessUnitCopyStatus TryCopyCompletedOutput(
            in NvencAccessUnitWriteLease writeLease,
            in NvencEncodeSampleSlotLease sampleSlot,
            INvencOutputBitstreamSource source,
            out NvencAccessUnitCopyProof proof)
        {
            proof = default;

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

                if (_contentReady || _copyInProgress)
                {
                    return NvencAccessUnitCopyStatus.NotStarted;
                }

                _copyInProgress = true;
                _copyInFlight = true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            // External call: completion wait, lock, copy, unlock, unmap.
            if (!source.TryCopyCompletedOutput(_workToken, sampleSlot, _storage, _storage.Length, out int validLength))
            {
                _copyInFlight = false;
                return NvencAccessUnitCopyStatus.Rejected;
            }

            // The external copy has returned: release and re-reservation may
            // proceed again.
            _copyInFlight = false;

            if (validLength <= 0 || validLength > _storage.Length)
            {
                return NvencAccessUnitCopyStatus.Rejected;
            }

            // Commit the valid length behind a short gate. The failure path
            // writes no buffer field; on gate contention a generation-bound
            // proof carries the length for a later commit.
            if (!_processState.TryBeginResourceResolution())
            {
                proof = new NvencAccessUnitCopyProof(_ownerToken, _generation, validLength, _copyNonce);
                return NvencAccessUnitCopyStatus.Pending;
            }

            try
            {
                // The lease and claim must still be exact: a concurrent release
                // and re-reservation during the source call invalidates this
                // copy for the current generation.
                if (!IsExactCollector(writeLease) || !_copyInProgress)
                {
                    return NvencAccessUnitCopyStatus.NotStarted;
                }

                _validLength = validLength;
                _contentReady = true;
                return NvencAccessUnitCopyStatus.Committed;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Commits a deferred copy behind the resource-resolution gate. The
        /// exact write lease and the generation-bound proof are re-verified
        /// inside the gate before <c>_validLength</c> and <c>_contentReady</c>
        /// are updated, so a length that never came from a successful source
        /// call cannot be committed. Returns <c>false</c> while the gate is
        /// held, the process is poisoned, the lease is no longer exact, or the
        /// proof does not match the current generation.
        /// </summary>
        internal bool TryCommitCopiedContent(
            in NvencAccessUnitWriteLease writeLease,
            in NvencAccessUnitCopyProof proof)
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

                if (!proof.Matches(_ownerToken, _generation, _copyNonce, _storage.Length, out int validLength))
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
        /// Sink-side length query for the checked chunk capacity decision. The
        /// exact owned lease and content-ready state are verified inside the
        /// gate; the backing array is never exposed. Returns Ready with the
        /// valid length, Busy on gate contention, or Invalid on an ownership
        /// break.
        /// </summary>
        internal NvencOwnedAccessUnitBoundaryStatus TryGetValidLength(
            in NvencOwnedAccessUnitLease ownedLease,
            out int validLength)
        {
            validLength = 0;

            if (!_processState.TryBeginResourceResolution())
            {
                return NvencOwnedAccessUnitBoundaryStatus.Busy;
            }

            try
            {
                if (!IsExactSink(ownedLease) || !_contentReady || _contentConsumed || _consumeInFlight)
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                validLength = _validLength;
                return NvencOwnedAccessUnitBoundaryStatus.Ready;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Sink-side synchronous consume. The exact owned lease is validated
        /// and the consume is claimed inside the gate; the injected appender is
        /// then called outside the gate with the fixed storage and recorded
        /// length, which are valid only for the duration of the call. The
        /// consumer return is committed inside a second gate that re-verifies
        /// the same lease, generation, and phase: the in-flight claim is
        /// lowered and a successful append marks the content consumed, so the
        /// same lease cannot re-run the consumer. If the second gate is
        /// contended the claim stays held and a generation-bound proof carries
        /// the outcome for a later completion; a writer exception or an unknown
        /// outcome also leaves the claim held so the region can never be
        /// returned or reused while the result is unknown.
        /// </summary>
        internal NvencOwnedAccessUnitBoundaryStatus TryConsumeSinkContent(
            in NvencOwnedAccessUnitLease ownedLease,
            INvencRunChunkAppender writer,
            out NvencRunChunkAppendOutcome outcome,
            out NvencAccessUnitConsumeProof proof)
        {
            outcome = default;
            proof = default;

            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return NvencOwnedAccessUnitBoundaryStatus.Busy;
            }

            try
            {
                if (!IsExactSink(ownedLease) || !_contentReady || _contentConsumed || _consumeInFlight)
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                _consumeInFlight = true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            // External consumer call: the fixed storage and recorded length are
            // valid only for the duration of this call. A writer exception
            // propagates with the consume claim still held; the sink poisons
            // and the region is never returned or reused.
            outcome = writer.Append(_storage, 0, _validLength);

            // Commit the consumer return inside the same gate that re-verifies
            // the exact lease. On gate contention the claim stays held and a
            // generation-bound proof carries the outcome and length for a later
            // completion.
            if (!_processState.TryBeginResourceResolution())
            {
                proof = new NvencAccessUnitConsumeProof(
                    _ownerToken, _generation, _validLength, outcome, _consumeNonce);
                return NvencOwnedAccessUnitBoundaryStatus.Deferred;
            }

            try
            {
                if (!IsExactSink(ownedLease))
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                if (outcome != NvencRunChunkAppendOutcome.Appended &&
                    outcome != NvencRunChunkAppendOutcome.RejectedBeforeWrite)
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                _consumeInFlight = false;
                if (outcome == NvencRunChunkAppendOutcome.Appended)
                {
                    _contentConsumed = true;
                }

                return NvencOwnedAccessUnitBoundaryStatus.Ready;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Completes a deferred consume return behind the gate. The exact owned
        /// lease, the held in-flight claim, and the generation-bound proof are
        /// re-verified inside the gate before the claim is lowered and the
        /// content is marked consumed, so an outcome that never came from a
        /// writer call cannot be committed. Returns Ready on completion,
        /// Deferred while the gate is held or poisoned, or Invalid on an
        /// ownership break.
        /// </summary>
        internal NvencOwnedAccessUnitBoundaryStatus TryCompleteConsumeContent(
            in NvencOwnedAccessUnitLease ownedLease,
            in NvencAccessUnitConsumeProof proof)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return NvencOwnedAccessUnitBoundaryStatus.Deferred;
            }

            try
            {
                if (!IsExactSink(ownedLease))
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                if (!proof.Matches(_ownerToken, _generation, _consumeNonce, out NvencRunChunkAppendOutcome outcome, out _))
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                if (!_consumeInFlight)
                {
                    return NvencOwnedAccessUnitBoundaryStatus.Invalid;
                }

                _consumeInFlight = false;
                if (outcome == NvencRunChunkAppendOutcome.Appended)
                {
                    _contentConsumed = true;
                }

                return NvencOwnedAccessUnitBoundaryStatus.Ready;
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
        /// changing any field, and the release is refused while the external
        /// source copy is still in flight so the shared storage is never
        /// reused under a running copy.
        /// </summary>
        internal bool CancelWrite(in NvencAccessUnitWriteLease writeLease)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactCollector(writeLease) || _copyInFlight)
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
        /// changing any field, and the release is refused while a synchronous
        /// consume is still in flight.
        /// </summary>
        internal bool Return(in NvencOwnedAccessUnitLease ownedLease)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!IsExactSink(ownedLease) || _consumeInFlight)
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
            _copyInFlight = false;
            _consumeInFlight = false;
            _contentConsumed = false;
        }
    }
}
