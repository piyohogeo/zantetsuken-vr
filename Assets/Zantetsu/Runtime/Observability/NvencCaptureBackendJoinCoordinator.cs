using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Phase 0.11 Backend Join boundary for one Run chunk. It is bound at
    /// construction to the exact process state, Submit Worker, Output Worker,
    /// Run chunk context, Main Thread Texture teardown, and the backend's
    /// resource pools, Access Unit buffer, and Output Processor, and publishes
    /// a normal join exactly once only after every normal resource is resolved
    /// and both workers have been physically stopped and disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryJoin"/> is non-waiting and idempotent. It succeeds only
    /// while the process is Draining and not Poisoned, the exact Main Thread
    /// Texture teardown receipt is valid for this Run, the exact Submit Worker
    /// reports <c>DrainCompleted &amp;&amp; IsStopped</c> with no fatal
    /// failure, the exact Output Worker reports
    /// <c>TeardownCompleted &amp;&amp; IsStopped</c> with no fatal failure,
    /// every Work Slot, Encode Sample Slot, GPU Conversion Sync,
    /// Submit-to-Output credit, and Frame Completion credit is unoccupied, the
    /// Owned Access Unit is Free, and the Output Processor holds no current or
    /// pending record. A not-ready state returns false with no side effect;
    /// Poison performs no resource return, no dispose, and no join.
    /// </para>
    /// <para>
    /// On success the Output Worker is disposed first and the Submit Worker
    /// second, each at most once. A dispose failure or a broken correlation
    /// poisons and propagates the original exception; a partially-disposed
    /// state is never later retried into a successful join.
    /// </para>
    /// <para>
    /// This type owns no wait primitive, queue, file, native call, token,
    /// nonce, or ownership lease, and it never contacts the Run chunk beyond
    /// the construction-time and re-check correlation predicate. It uses no
    /// sleep, spin, blocking wait, or pooled asynchronous primitive.
    /// </para>
    /// </remarks>
    internal sealed class NvencCaptureBackendJoinCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOrderedSubmitWorkerService _submitWorker;
        private readonly NvencOrderedOutputWorkerService _outputWorker;
        private readonly NvencRunChunkContext _context;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencGpuConversionSyncPool _gpuConversionSyncSlots;
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;
        private readonly NvencOwnedAccessUnitBuffer _buffer;
        private readonly NvencOrderedOutputProcessor _outputProcessor;
        private readonly INvencMainThreadTextureTeardown _mainThreadTextureTeardown;

        private bool _joined;

        internal NvencCaptureBackendJoinCoordinator(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencOrderedOutputWorkerService outputWorker,
            NvencRunChunkContext context,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool gpuConversionSyncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits,
            NvencOwnedAccessUnitBuffer buffer,
            NvencOrderedOutputProcessor outputProcessor,
            INvencMainThreadTextureTeardown mainThreadTextureTeardown)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _submitWorker = submitWorker ?? throw new ArgumentNullException(nameof(submitWorker));
            _outputWorker = outputWorker ?? throw new ArgumentNullException(nameof(outputWorker));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _workSlots = workSlots ?? throw new ArgumentNullException(nameof(workSlots));
            _sampleSlots = sampleSlots ?? throw new ArgumentNullException(nameof(sampleSlots));
            _gpuConversionSyncSlots = gpuConversionSyncSlots ?? throw new ArgumentNullException(nameof(gpuConversionSyncSlots));
            _submitToOutputCredits = submitToOutputCredits ?? throw new ArgumentNullException(nameof(submitToOutputCredits));
            _frameCompletionCredits = frameCompletionCredits ?? throw new ArgumentNullException(nameof(frameCompletionCredits));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _outputProcessor = outputProcessor ?? throw new ArgumentNullException(nameof(outputProcessor));
            _mainThreadTextureTeardown = mainThreadTextureTeardown ?? throw new ArgumentNullException(nameof(mainThreadTextureTeardown));

            if (!ReferenceEquals(_submitWorker.ProcessState, _processState))
            {
                throw new ArgumentException(
                    "The Submit Worker must be bound to the exact process state.", nameof(submitWorker));
            }

            if (!_outputWorker.IsCorrelatedWith(_processState, _submitWorker, _context))
            {
                throw new ArgumentException(
                    "The Output Worker must be bound to the exact process state, Submit Worker, and Run chunk context.",
                    nameof(outputWorker));
            }

            if (!ReferenceEquals(_context.ProcessState, _processState))
            {
                throw new ArgumentException(
                    "The Run chunk context must be bound to the exact process state.", nameof(context));
            }

            if (!_outputProcessor.IsCorrelatedWith(_processState, _submitWorker, _context))
            {
                throw new ArgumentException(
                    "The Output Processor must be bound to the exact process state, Submit Worker, and Run chunk context.",
                    nameof(outputProcessor));
            }

            if (!_mainThreadTextureTeardown.IsBoundTo(_context))
            {
                throw new ArgumentException(
                    "The Main Thread texture teardown must be bound to the exact Run chunk context.", nameof(mainThreadTextureTeardown));
            }

            // The inspected pools and buffer must be the exact resources used
            // by the real Submit and Output pipelines, so a foreign empty pool
            // can never mask a reservation in the real pipeline.
            if (!_submitWorker.IsCorrelatedWithResources(
                    workSlots, sampleSlots, gpuConversionSyncSlots, submitToOutputCredits, frameCompletionCredits))
            {
                throw new ArgumentException(
                    "The Submit Worker must be bound to the exact Work, Sample, GPU Conversion Sync, Submit-to-Output credit, and Frame Completion credit pools.");
            }

            if (!_outputProcessor.IsCorrelatedWithResources(
                    workSlots, sampleSlots, submitToOutputCredits, frameCompletionCredits, buffer))
            {
                throw new ArgumentException(
                    "The Output Processor must be bound to the exact Work, Sample, Submit-to-Output credit, Frame Completion credit pools, and Owned Access Unit buffer.");
            }
        }

        /// <summary>
        /// O(1) exact-reference correlation predicate: true only when this
        /// join boundary is bound to the exact process state, Submit Worker,
        /// Output Worker, and Run chunk context.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencOrderedOutputWorkerService outputWorker,
            NvencRunChunkContext context)
        {
            return ReferenceEquals(_processState, processState)
                && ReferenceEquals(_submitWorker, submitWorker)
                && ReferenceEquals(_outputWorker, outputWorker)
                && ReferenceEquals(_context, context);
        }

        /// <summary>
        /// Non-waiting, idempotent normal join. Succeeds only when the exact
        /// Main Thread Texture teardown receipt is valid for this Run, the
        /// process is Draining and not Poisoned, both workers are drained,
        /// teardown completed, physically stopped, and free of fatal failure,
        /// and every backend resource is resolved to zero. On success both
        /// workers are disposed at most once and the join is published. A
        /// not-ready state returns false with no side effect. The whole check
        /// and dispose run inside the shared process-state gate so a concurrent
        /// Poison either linearizes first (false, no side effect) or waits
        /// behind this join.
        /// </summary>
        internal bool TryJoin(NvencMainThreadTextureTeardownReceipt textureTeardownReceipt)
        {
            // Serialize the entire join with the Poison transition on the
            // shared short gate. The gate is reentrant, so the coordinator's
            // outer acquisition remains safe under this inner acquisition.
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_joined)
                {
                    return true;
                }

                if (!_processState.IsDraining || _processState.IsPoisoned)
                {
                    return false;
                }

                // The exact Main Thread Texture teardown receipt is part of the
                // join precondition: without it, a foreign or unfinished
                // Texture teardown can never be reported as joined.
                if (textureTeardownReceipt == null ||
                    !textureTeardownReceipt.IsIssuedFor(_mainThreadTextureTeardown, _context))
                {
                    return false;
                }

                if (!_submitWorker.DrainCompleted || !_submitWorker.IsStopped || _submitWorker.TryGetFailure(out _))
                {
                    return false;
                }

                if (!_outputWorker.TeardownCompleted || !_outputWorker.IsStopped || _outputWorker.TryGetFailure(out _))
                {
                    return false;
                }

                if (_workSlots.OccupiedCount != 0 ||
                    _sampleSlots.OccupiedCount != 0 ||
                    _gpuConversionSyncSlots.OccupiedCount != 0 ||
                    _submitToOutputCredits.OccupiedCount != 0 ||
                    _frameCompletionCredits.OccupiedCount != 0)
                {
                    return false;
                }

                if (_buffer.Phase != NvencAccessUnitPhase.Free)
                {
                    return false;
                }

                if (_outputProcessor.HasPendingWork)
                {
                    return false;
                }

                // Dispose the Output Worker first and the Submit Worker second,
                // each exactly once. A dispose failure poisons and propagates
                // the original exception; the join is never published
                // afterwards.
                try
                {
                    _outputWorker.Dispose();
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                try
                {
                    _submitWorker.Dispose();
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                _joined = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// True only after <see cref="TryJoin"/> has succeeded once.
        /// </summary>
        internal bool Joined => _joined;
    }
}
