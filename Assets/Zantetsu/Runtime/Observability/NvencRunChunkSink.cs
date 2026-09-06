using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Run chunk sink for the Phase 0.11 Output Worker. It appends
    /// the content of one exact owned Access Unit lease to a single pre-opened
    /// Run chunk in accepted FIFO order, updates the checked append count,
    /// accumulated byte length, and last Capture Frame Id, and returns the
    /// owned region exactly once. It owns no queue, worker, task, thread,
    /// drain, join, finalize, rename, descriptor, or Frame Completion duty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The success order is fixed: validate the positive, strictly increasing
    /// frame id; check the checked accumulated length against
    /// <see cref="NvencBringUpProfileV1.MaxChunkByteLength"/> before any writer
    /// contact; synchronously consume the buffer through the injected
    /// <see cref="INvencRunChunkAppender"/>; return the owned region exactly
    /// once; then advance the counters. A known pre-write rejection or a
    /// writer <see cref="NvencRunChunkAppendOutcome.RejectedBeforeWrite"/>
    /// returns the region and issues ControlledFailure. A partial or unknown
    /// write, a writer exception, a failed return, or a stale lease poisons the
    /// process without guessing a return.
    /// </para>
    /// <para>
    /// A transient resource-resolution gate contention parks the single pending
    /// work token, owned lease, resume stage, outcome, and valid length, to be
    /// completed on the next attempt without re-running the writer. The parked
    /// work token and owned lease are matched exactly on resume, and a mismatch
    /// poisons as an order or ownership break. This type owns no thread, queue,
    /// or native resource and performs no run-time managed allocation.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunChunkSink
    {
        private enum NvencRunChunkSinkPendingStage
        {
            ConsumeNotStarted,
            ConsumeDeferred,
            ConsumeCommitted,
        }

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOwnedAccessUnitBuffer _buffer;
        private readonly INvencRunChunkAppender _writer;

        private long _appendedCount;
        private long _accumulatedByteLength;
        private long _lastFrameId;

        private bool _pending;
        private CaptureFrameWorkToken _pendingWorkToken;
        private NvencOwnedAccessUnitLease _pendingOwnedLease;
        private NvencRunChunkSinkPendingStage _pendingStage;
        private NvencRunChunkAppendOutcome _pendingOutcome;
        private int _pendingValidLength;
        private NvencOwnedAccessUnitBuffer.NvencAccessUnitConsumeProof _pendingProof;

        internal NvencRunChunkSink(
            NvencCaptureProcessState processState,
            NvencOwnedAccessUnitBuffer buffer,
            INvencRunChunkAppender writer)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        internal long AppendedCount => _appendedCount;

        internal long AccumulatedByteLength => _accumulatedByteLength;

        internal long LastFrameId => _lastFrameId;

        internal bool TryAppend(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            if (_pending)
            {
                return CompletePending(workToken, ownedLease, out result);
            }

            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!workToken.IsValid || !ownedLease.IsValid)
            {
                return false;
            }

            // The presented work token must be the exact token bound to the
            // owned lease; a mismatch is an ownership break, never a
            // controllable path, and must poison before any writer contact.
            if (!ownedLease.WorkToken.IdenticalTo(workToken))
            {
                PoisonAndThrow("Run Chunk Sink work token and owned lease are not correlated.");
            }

            return CompleteAppend(workToken, ownedLease, out result);
        }

        private bool CompleteAppend(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            // The frame id must be positive and strictly increasing across
            // successful appends; a violation is a known pre-write rejection.
            if (workToken.CaptureFrameId <= 0 ||
                (_appendedCount > 0 && workToken.CaptureFrameId <= _lastFrameId))
            {
                return CompleteControlledFailure(workToken, ownedLease, out result);
            }

            // Checked accumulated length, before any writer contact.
            NvencOwnedAccessUnitBoundaryStatus lengthStatus = _buffer.TryGetValidLength(ownedLease, out int validLength);
            switch (lengthStatus)
            {
                case NvencOwnedAccessUnitBoundaryStatus.Ready:
                    if ((long)validLength + _accumulatedByteLength > NvencBringUpProfileV1.MaxChunkByteLength)
                    {
                        return CompleteControlledFailure(workToken, ownedLease, out result);
                    }
                    break;

                case NvencOwnedAccessUnitBoundaryStatus.Busy:
                    if (_processState.IsPoisoned)
                    {
                        PoisonAndThrow("Run Chunk Sink was poisoned before the append.");
                    }
                    Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeNotStarted, default, 0, default);
                    return false;

                default:
                    PoisonAndThrow("Run Chunk Sink owned Access Unit lease is not consumable.");
                    return false;
            }

            // Synchronous consume; a writer exception poisons and propagates the
            // same instance, leaving the consume claim held.
            NvencOwnedAccessUnitBoundaryStatus consumeStatus;
            NvencRunChunkAppendOutcome outcome;
            NvencOwnedAccessUnitBuffer.NvencAccessUnitConsumeProof proof;
            try
            {
                consumeStatus = _buffer.TryConsumeSinkContent(ownedLease, _writer, out outcome, out proof);
            }
            catch
            {
                _processState.TryPoison();
                throw;
            }

            switch (consumeStatus)
            {
                case NvencOwnedAccessUnitBoundaryStatus.Ready:
                    return CompleteConsumed(workToken, ownedLease, validLength, outcome, out result);

                case NvencOwnedAccessUnitBoundaryStatus.Deferred:
                    Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeDeferred,
                        outcome, validLength, proof);
                    return false;

                case NvencOwnedAccessUnitBoundaryStatus.Busy:
                    if (_processState.IsPoisoned)
                    {
                        PoisonAndThrow("Run Chunk Sink was poisoned during the append.");
                    }
                    Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeNotStarted, default, 0, default);
                    return false;

                default:
                    PoisonAndThrow("Run Chunk Sink owned Access Unit lease became invalid during the append.");
                    return false;
            }
        }

        private bool CompleteConsumed(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            int validLength,
            NvencRunChunkAppendOutcome outcome,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            switch (outcome)
            {
                case NvencRunChunkAppendOutcome.Appended:
                    return CompleteAppended(workToken, ownedLease, validLength, out result);

                case NvencRunChunkAppendOutcome.RejectedBeforeWrite:
                    return CompleteControlledFailure(workToken, ownedLease, out result);

                default:
                    PoisonAndThrow("Run Chunk Sink append result is indeterminate.");
                    return false;
            }
        }

        private bool CompleteAppended(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            int validLength,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            if (_processState.IsPoisoned)
            {
                PoisonAndThrow("Run Chunk Sink was poisoned before the post-append commit.");
            }

            // A busy gate here is ordinary contention, not unknown ownership:
            // park and complete later without re-running the writer.
            if (!_processState.TryBeginResourceResolution())
            {
                Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeCommitted,
                    NvencRunChunkAppendOutcome.Appended, validLength, default);
                return false;
            }

            try
            {
                if (!_buffer.Return(ownedLease))
                {
                    PoisonAndThrow("Run Chunk Sink owned Access Unit return failed after a known-success append.");
                }

                // The return, the counter advances, and the terminal result are
                // committed inside the same resource-resolution gate, so a
                // concurrent poison cannot interleave a released buffer with a
                // later success commit.
                _appendedCount++;
                _accumulatedByteLength += validLength;
                _lastFrameId = workToken.CaptureFrameId;

                result = NvencRunChunkSinkResult.Appended(workToken, validLength);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool CompleteControlledFailure(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            if (_processState.IsPoisoned)
            {
                PoisonAndThrow("Run Chunk Sink was poisoned before the controlled release.");
            }

            if (!_processState.TryBeginResourceResolution())
            {
                Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeCommitted,
                    NvencRunChunkAppendOutcome.RejectedBeforeWrite, 0, default);
                return false;
            }

            try
            {
                if (!_buffer.Return(ownedLease))
                {
                    PoisonAndThrow("Run Chunk Sink owned Access Unit return failed during the controlled release.");
                }

                // The return and the terminal result are committed inside the
                // same resource-resolution gate.
                result = NvencRunChunkSinkResult.ControlledFailure(workToken);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool CompleteCommitted(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            NvencRunChunkAppendOutcome outcome,
            int validLength,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            switch (outcome)
            {
                case NvencRunChunkAppendOutcome.Appended:
                    return CompleteAppended(workToken, ownedLease, validLength, out result);

                case NvencRunChunkAppendOutcome.RejectedBeforeWrite:
                    return CompleteControlledFailure(workToken, ownedLease, out result);

                default:
                    PoisonAndThrow("Run Chunk Sink committed outcome is indeterminate.");
                    return false;
            }
        }

        private bool CompleteDeferred(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            NvencRunChunkAppendOutcome outcome,
            int validLength,
            NvencOwnedAccessUnitBuffer.NvencAccessUnitConsumeProof proof,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            // Complete the deferred consumer return behind the gate. The claim
            // stays held until this completes, so the region cannot be returned
            // or reused before the outcome is committed.
            NvencOwnedAccessUnitBoundaryStatus completeStatus =
                _buffer.TryCompleteConsumeContent(ownedLease, proof);

            switch (completeStatus)
            {
                case NvencOwnedAccessUnitBoundaryStatus.Ready:
                    break;

                case NvencOwnedAccessUnitBoundaryStatus.Deferred:
                    if (_processState.IsPoisoned)
                    {
                        PoisonAndThrow("Run Chunk Sink was poisoned before the deferred consume completion.");
                    }
                    Park(workToken, ownedLease, NvencRunChunkSinkPendingStage.ConsumeDeferred,
                        outcome, validLength, proof);
                    return false;

                default:
                    PoisonAndThrow("Run Chunk Sink deferred consume completion failed.");
                    return false;
            }

            return CompleteCommitted(workToken, ownedLease, outcome, validLength, out result);
        }

        private bool CompletePending(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            out NvencRunChunkSinkResult result)
        {
            result = default;

            if (!MatchesPending(workToken, ownedLease))
            {
                PoisonAndThrow("Run Chunk Sink pending record mismatch.");
            }

            CaptureFrameWorkToken parkedToken = _pendingWorkToken;
            NvencOwnedAccessUnitLease parkedLease = _pendingOwnedLease;
            NvencRunChunkSinkPendingStage stage = _pendingStage;
            NvencRunChunkAppendOutcome outcome = _pendingOutcome;
            int validLength = _pendingValidLength;
            NvencOwnedAccessUnitBuffer.NvencAccessUnitConsumeProof proof = _pendingProof;

            bool terminal;
            switch (stage)
            {
                case NvencRunChunkSinkPendingStage.ConsumeNotStarted:
                    terminal = CompleteAppend(parkedToken, parkedLease, out result);
                    break;

                case NvencRunChunkSinkPendingStage.ConsumeDeferred:
                    terminal = CompleteDeferred(parkedToken, parkedLease, outcome, validLength, proof, out result);
                    break;

                default:
                    terminal = CompleteCommitted(parkedToken, parkedLease, outcome, validLength, out result);
                    break;
            }

            if (terminal)
            {
                ClearPending();
            }

            return terminal;
        }

        private void Park(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            NvencRunChunkSinkPendingStage stage,
            NvencRunChunkAppendOutcome outcome,
            int validLength,
            NvencOwnedAccessUnitBuffer.NvencAccessUnitConsumeProof proof)
        {
            _pending = true;
            _pendingWorkToken = workToken;
            _pendingOwnedLease = ownedLease;
            _pendingStage = stage;
            _pendingOutcome = outcome;
            _pendingValidLength = validLength;
            _pendingProof = proof;
        }

        private void ClearPending()
        {
            _pending = false;
            _pendingWorkToken = default;
            _pendingOwnedLease = default;
            _pendingStage = NvencRunChunkSinkPendingStage.ConsumeNotStarted;
            _pendingOutcome = default;
            _pendingValidLength = 0;
            _pendingProof = default;
        }

        private bool MatchesPending(in CaptureFrameWorkToken workToken, in NvencOwnedAccessUnitLease ownedLease)
        {
            CaptureFrameWorkToken token = _pendingWorkToken;
            NvencOwnedAccessUnitLease lease = _pendingOwnedLease;

            return token.IdenticalTo(workToken) &&
                lease.OwnerToken == ownedLease.OwnerToken &&
                lease.Generation == ownedLease.Generation &&
                lease.WorkToken.IdenticalTo(ownedLease.WorkToken);
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
