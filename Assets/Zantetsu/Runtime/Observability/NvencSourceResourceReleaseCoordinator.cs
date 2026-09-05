using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Early release boundary for the Phase 0.11 source surface and GPU
    /// conversion sync credit. The Submit Worker only hands off an evidenced
    /// release request; the Main Thread later applies it under the same short
    /// resource-resolution gate, returning the exact sync credit and releasing
    /// the exact source surface in one critical section. Work and Sample Slots
    /// are never released here. The main-thread-only render target pool is
    /// therefore never touched from the Submit Worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worker side verifies the record's current correlation, observes the
    /// source read completion exactly once, binds the evidence to the exact
    /// source, work, sync credit, and surface, and then hands the request off
    /// without waiting and without releasing anything. The main thread side
    /// fetches one retained handoff, re-acquires the same gate, re-checks that
    /// the process is not poisoned and that the record and evidence still
    /// correlate, and only then returns the sync credit and releases the
    /// surface in the same short critical section. A poison race therefore
    /// either linearizes the apply first (both resources freed) or poison first
    /// (both retained, and the handoff is never lost) with no partial release.
    /// A busy gate or a full handoff boundary fails without waiting and leaves
    /// everything unchanged so the caller can retry later.
    /// </para>
    /// <para>
    /// This coordinator never creates a Submit-to-Output record, never performs
    /// a frame completion, never submits to the encoder, and performs no
    /// allocation, waiting, file I/O, or native call in the release path.
    /// </para>
    /// </remarks>
    internal sealed class NvencSourceResourceReleaseCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencGpuConversionSyncPool _syncSlots;
        private readonly INvencSourceReadCompletedSource _completionSource;
        private readonly NvencSourceSurfaceReturnBoundary _surfaceReturnBoundary;
        private readonly Guid _backendOwner;

        internal NvencSourceResourceReleaseCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            INvencSourceReadCompletedSource completionSource,
            NvencSourceSurfaceReturnBoundary surfaceReturnBoundary,
            Guid backendOwner)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            if (workSlots == null)
            {
                throw new ArgumentNullException(nameof(workSlots));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            if (syncSlots == null)
            {
                throw new ArgumentNullException(nameof(syncSlots));
            }

            if (completionSource == null)
            {
                throw new ArgumentNullException(nameof(completionSource));
            }

            if (surfaceReturnBoundary == null)
            {
                throw new ArgumentNullException(nameof(surfaceReturnBoundary));
            }

            if (backendOwner == Guid.Empty)
            {
                throw new ArgumentException("Backend owner must not be empty.", nameof(backendOwner));
            }

            _processState = processState;
            _workSlots = workSlots;
            _sampleSlots = sampleSlots;
            _syncSlots = syncSlots;
            _completionSource = completionSource;
            _surfaceReturnBoundary = surfaceReturnBoundary;
            _backendOwner = backendOwner;
        }

        /// <summary>
        /// Submit Worker side: verify the record and evidence and hand off an
        /// evidenced release request without releasing the sync credit or the
        /// surface. Returns false without handing anything off when the record
        /// is stale, the source read is not completed, the evidence does not
        /// match, the process is poisoned, the gate is busy, or the boundary is
        /// full; the caller may retry later.
        /// </summary>
        internal bool TryReleaseSourceResources(in NvencSubmissionRecord record)
        {
            // 1. Verify the record's current correlation against the exact pools.
            if (!record.IsValidFor(_backendOwner, _workSlots, _sampleSlots, _syncSlots))
            {
                return false;
            }

            // 2. Observe the source read completion exactly once.
            if (!_completionSource.TryGetEvidence(record, out NvencSourceReadCompletedEvidence evidence))
            {
                // 3. Not completed: change nothing.
                return false;
            }

            // The evidence must bind the exact source, work, sync credit, and surface.
            if (!evidence.Matches(_completionSource, record))
            {
                return false;
            }

            // 4. Acquire the short resource-resolution gate without waiting to
            // serialize the verification and handoff with poison and transitions.
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // 5. Re-check inside the gate: the process is not poisoned and the
                // record and evidence still correlate.
                if (_processState.IsPoisoned ||
                    !record.IsValidFor(_backendOwner, _workSlots, _sampleSlots, _syncSlots) ||
                    !evidence.Matches(_completionSource, record))
                {
                    return false;
                }

                // 6. Transition the active sync credit to pending exactly once.
                // An already-pending credit is rejected here, so the same record
                // cannot be handed off twice.
                if (!_syncSlots.TryMarkPendingRelease(record.SyncSlot))
                {
                    return false;
                }

                // 7. Hand off the evidenced release request. Nothing is released
                // here; the Main Thread applies it under the same gate.
                if (!_surfaceReturnBoundary.TryEnqueue(
                    new NvencSourceSurfaceReleaseHandoff(record, evidence)))
                {
                    // The boundary is full: revert the pending transition so the
                    // credit can be handed off again later.
                    _syncSlots.RevertPendingRelease(record.SyncSlot);
                    return false;
                }

                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Main Thread side: apply one retained handoff under the same short
        /// resource-resolution gate. Returns false while there is nothing
        /// pending, the gate is busy, or the process is poisoned, in which case
        /// the handoff is retained so it is never lost. On success the exact
        /// source surface and the exact sync credit are both released inside the
        /// same critical section.
        /// </summary>
        internal bool TryApplyPendingRelease()
        {
            if (!_surfaceReturnBoundary.TryGetPending(out NvencSourceSurfaceReleaseHandoff handoff))
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // Re-check the process state and the record/evidence correlation
                // inside the gate; poison serializes on the same gate.
                if (_processState.IsPoisoned ||
                    !handoff.Record.IsValidFor(_backendOwner, _workSlots, _sampleSlots, _syncSlots) ||
                    !handoff.Evidence.Matches(_completionSource, handoff.Record))
                {
                    return false;
                }

                handoff.Record.Surface.ReleaseFromBackend(_backendOwner, handoff.Record.WorkToken);

                if (!_syncSlots.TryReturnPendingRelease(handoff.Record.SyncSlot))
                {
                    throw new InvalidOperationException(
                        "Pending sync credit return failed inside the resource-resolution gate; internal invariant violated.");
                }

                _surfaceReturnBoundary.CompletePending();
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }
    }
}
