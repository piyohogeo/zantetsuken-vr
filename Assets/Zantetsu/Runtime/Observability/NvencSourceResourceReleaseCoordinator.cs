using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Early release boundary for the Phase 0.11 source surface and GPU
    /// conversion sync credit. Once a work's source read has completed, this
    /// coordinator releases the backend-owned source surface and returns the
    /// exact sync credit; it never releases the Work Slot or the Encode Sample
    /// Slot, which stay reserved for the later stages of the submission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The release follows a fixed order: the record's current correlation is
    /// verified against the exact pools, the source read completion is observed
    /// exactly once, the evidence is bound to the exact source, work, sync
    /// credit, and surface, and only then a short resource-resolution gate is
    /// acquired without waiting. Inside that gate the process is re-checked for
    /// poison, the record and evidence are re-verified, the surface is released,
    /// and the sync credit is returned. Success is reported only when both
    /// releases completed, so a poison race either linearizes the release first
    /// (both resources freed) or poison first (both retained) with no partial
    /// release. A busy gate fails without waiting and leaves the record
    /// unchanged so the caller can retry later.
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
        private readonly Guid _backendOwner;

        internal NvencSourceResourceReleaseCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            INvencSourceReadCompletedSource completionSource,
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

            if (backendOwner == Guid.Empty)
            {
                throw new ArgumentException("Backend owner must not be empty.", nameof(backendOwner));
            }

            _processState = processState;
            _workSlots = workSlots;
            _sampleSlots = sampleSlots;
            _syncSlots = syncSlots;
            _completionSource = completionSource;
            _backendOwner = backendOwner;
        }

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

            // 4. Acquire the short resource-resolution gate without waiting.
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // 5. Re-check inside the gate: the process is not poisoned and the
                // record and evidence still correlate. Poison and other releases
                // serialize on the same gate, so this region is atomic with
                // respect to them.
                if (_processState.IsPoisoned ||
                    !record.IsValidFor(_backendOwner, _workSlots, _sampleSlots, _syncSlots) ||
                    !evidence.Matches(_completionSource, record))
                {
                    return false;
                }

                // 6. Release the exact source surface.
                record.Surface.ReleaseFromBackend(_backendOwner, record.WorkToken);

                // 7. Return the exact sync credit. Step 5 re-verified the credit
                // is active, and the state is Running or Draining while the gate
                // is held, so this cannot fail.
                if (!_syncSlots.TryReturn(record.SyncSlot))
                {
                    throw new InvalidOperationException(
                        "Sync credit return failed while holding the resource-resolution gate; internal invariant violated.");
                }

                // 8. Both releases succeeded.
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }
    }
}
