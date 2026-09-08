using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Phase 0.11 Main Thread Run coordinator for one Run chunk. It connects
    /// the frozen Accepted Snapshot to the existing Submit/Output Worker
    /// terminal boundary: it starts the process-wide drain, fixes the Accepted
    /// Snapshot, requests the Submit Worker drain, reflects Main-Thread
    /// finalized Frame Completions in accepted order, and only then requests
    /// Finalize or Abandon from the exact Output Worker and registers the
    /// finalized result into the exact local Registry Slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry is non-waiting: it never sleeps, never spins, and never
    /// waits for the workers. A not-yet-ready condition (Submit Worker still
    /// draining, or not all Frame Completions reflected) returns false with
    /// the held state kept, so the caller can retry on the next explicit
    /// progress call. The terminal request is issued to the exact Output
    /// Worker at most once; the finalized result is registered exactly once.
    /// </para>
    /// <para>
    /// Frame Completion reflection is the Main Thread's post-reflection entry:
    /// it records each valid, zero-artifact completion strictly against the
    /// next expected element of the frozen snapshot, so a duplicate, backward,
    /// foreign, or over-capacity completion poisons the process without
    /// fabricating a terminal result. Before the drain it is rejected without
    /// change.
    /// </para>
    /// <para>
    /// This type holds no thread, queue, filesystem, native call, Plan,
    /// Publication, Trace, or ownership lease duty. It does not physically
    /// stop the Output Worker itself: after the terminal is collected and
    /// registered once, it requests the Output Worker's normal teardown at
    /// most once and leaves the worker to complete the teardown and stop on
    /// its own thread. Resource zero, backend-wide <c>TryJoin</c>, and Trace
    /// seal remain later units.
    /// </para>
    /// </remarks>
    internal sealed class NvencCaptureRunCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOrderedSubmitWorkerService _submitWorker;
        private readonly NvencOrderedOutputWorkerService _outputWorker;
        private readonly NvencRunChunkContext _context;
        private readonly NvencRunLocalRegistrySlot _registrySlot;
        private readonly INvencMainThreadTextureTeardown _mainThreadTextureTeardown;

        private NvencRunAcceptedFrameSnapshot _snapshot;
        private int _reflectedCount;
        private bool _allSucceeded = true;
        private bool _terminalRequested;
        private bool _requestedFinalize;
        private bool _terminalCollected;
        private bool _teardownRequested;
        private bool _mainThreadTextureTeardownCompleted;

        internal NvencCaptureRunCoordinator(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencOrderedOutputWorkerService outputWorker,
            NvencRunChunkContext context,
            NvencRunLocalRegistrySlot registrySlot,
            INvencMainThreadTextureTeardown mainThreadTextureTeardown)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _submitWorker = submitWorker ?? throw new ArgumentNullException(nameof(submitWorker));
            _outputWorker = outputWorker ?? throw new ArgumentNullException(nameof(outputWorker));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _registrySlot = registrySlot ?? throw new ArgumentNullException(nameof(registrySlot));
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

            if (!ReferenceEquals(_registrySlot.Context, _context))
            {
                throw new ArgumentException(
                    "The Registry Slot must be bound to the exact Run chunk context.", nameof(registrySlot));
            }

            if (!_mainThreadTextureTeardown.IsBoundTo(_context))
            {
                throw new ArgumentException(
                    "The Main Thread texture teardown must be bound to the exact Run chunk context.", nameof(mainThreadTextureTeardown));
            }
        }

        /// <summary>
        /// Non-waiting, idempotent drain entry. It advances Running to
        /// Draining, accepts an already Run-Abandoned Draining, fixes the
        /// Accepted Snapshot, requests the Submit Worker drain, and notifies
        /// the Output Worker. A re-call returns the exact same snapshot
        /// reference. Poison performs no progress and returns false. Once the
        /// process is Draining, a failure to fix the snapshot or request the
        /// Submit Worker drain is not rolled back to Running; it poisons and
        /// propagates.
        /// </summary>
        internal bool TryBeginDrain(out NvencRunAcceptedFrameSnapshot snapshot)
        {
            snapshot = null;

            // Serialize the Poison check and every state change with the
            // process-wide Poison transition on the shared short gate: a
            // concurrent poison either orders first (false, no change) or
            // waits behind this critical section.
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_snapshot != null)
                {
                    snapshot = _snapshot;
                    return true;
                }

                if (_processState.IsAccepting)
                {
                    if (!_processState.TryBeginDrain())
                    {
                        return false;
                    }
                }

                if (!_processState.IsDraining)
                {
                    return false;
                }

                if (!_context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot frozen))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Run chunk context could not freeze the accepted frame snapshot during drain.");
                }

                try
                {
                    if (!_submitWorker.BeginDrain())
                    {
                        throw new InvalidOperationException(
                            "Submit Worker rejected the drain request during drain.");
                    }

                    _outputWorker.Notify();
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                // Publish the success state only after the snapshot fix, the
                // Submit Worker drain request, and the Output Worker
                // notification have all completed, so a later-step failure is
                // never re-read as success.
                _snapshot = frozen;
                snapshot = frozen;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Main Thread post-reflection entry, called only after a Frame
        /// Completion has been successfully reflected into the existing Draft
        /// and Trace. Records the completion strictly in accepted order; the
        /// next expected Capture Frame Id is read from the frozen snapshot, so
        /// a duplicate, backward, foreign, invalid, over-capacity, or
        /// non-zero-artifact completion poisons the process. Before the drain
        /// it is rejected without change.
        /// </summary>
        internal bool TryReflectCompletion(in CaptureFrameCompletion completion)
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_snapshot == null)
                {
                    return false;
                }

                if (!completion.IsValid)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Frame Completion is invalid during Run chunk reflection.");
                }

                if (completion.WorkToken.TestRunId != _context.TestRunId)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Frame Completion TestRunId does not match the Run chunk context.");
                }

                if (completion.ProducedArtifactCount != 0)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Frame Completion produced a non-zero artifact count during Run chunk reflection.");
                }

                if (_reflectedCount >= _snapshot.Count)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "More Frame Completions were reflected than the frozen accepted snapshot holds.");
                }

                if (!_snapshot.TryGetCaptureFrameId(_reflectedCount, out long expected))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Frozen accepted snapshot read failed during Frame Completion reflection.");
                }

                if (completion.CaptureFrameId != expected)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Frame Completion CaptureFrameId does not match the frozen accepted order.");
                }

                if (completion.Status != CaptureFrameCompletionStatus.Succeeded)
                {
                    _allSucceeded = false;
                }

                _reflectedCount++;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Non-waiting, at-most-once terminal request. Finalize or Abandon is
        /// requested from the exact Output Worker only when the Accepted
        /// Snapshot is fixed, the exact Submit Worker reports
        /// <c>DrainCompleted</c>, and every accepted frame's Completion has
        /// been reflected. The choice is exactly: one-or-more accepted and all
        /// Succeeded requests Finalize; zero accepted or any Failed/Cancelled
        /// requests Abandon. A not-ready condition returns false with no
        /// request, for retry on the next explicit progress call.
        /// </summary>
        internal bool TryRequestTerminal()
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_snapshot == null || _terminalRequested)
                {
                    return false;
                }

                if (!_submitWorker.DrainCompleted)
                {
                    return false;
                }

                if (_reflectedCount != _snapshot.Count)
                {
                    return false;
                }

                bool finalize = _snapshot.Count >= 1 && _allSucceeded;

                bool accepted = finalize
                    ? _outputWorker.TryRequestFinalize()
                    : _outputWorker.TryRequestAbandon();

                if (!accepted)
                {
                    return false;
                }

                _terminalRequested = true;
                _requestedFinalize = finalize;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Non-waiting terminal collection. On a Finalized outcome it verifies
        /// the exact non-null valid result against the context's held result
        /// and registers it into the exact Registry Slot exactly once. On an
        /// Abandoned outcome it verifies no result and an Abandoned context,
        /// leaving the Registry Slot Empty. A variant mismatch, foreign or
        /// missing result, or registration failure poisons without converting
        /// to another outcome.
        /// </summary>
        internal bool TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome)
        {
            outcome = default;

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_terminalCollected)
                {
                    return false;
                }

                if (!_outputWorker.TryCollectTerminal(out outcome))
                {
                    return false;
                }

                if (!_terminalRequested || outcome.IsNone)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Run chunk terminal outcome arrived without a matching terminal request.");
                }

                if (_requestedFinalize != outcome.IsFinalized)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Run chunk terminal outcome does not match the requested terminal variant.");
                }

                if (outcome.IsFinalized)
                {
                    NvencChunkFinalizationResult result = outcome.Result;
                    if (result == null || !result.IsValid)
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "Run chunk finalized outcome carries no valid result.");
                    }

                    if (!_context.TryGetFinalizationResult(out NvencChunkFinalizationResult held) ||
                        !ReferenceEquals(held, result))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "Run chunk terminal result does not correlate to the context's held result.");
                    }

                    if (!_registrySlot.TryRegister(_context, result))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "Run chunk terminal result registration failed.");
                    }

                    _terminalCollected = true;
                    return true;
                }

                if (outcome.Result != null || _context.State != NvencRunChunkContextState.Abandoned)
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Run chunk abandoned outcome does not correlate to an abandoned context.");
                }

                _terminalCollected = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Non-waiting, at-most-once Output Worker teardown request. Admitted
        /// only after the terminal has been collected and registered exactly
        /// once (Finalized) or the Abandoned correlation has been confirmed,
        /// never while the terminal is uncollected, the process is Poisoned,
        /// or a teardown request was already accepted. A transient rejection
        /// by the Output Worker (for example a still-pending record) returns
        /// false for retry; once accepted the request is never re-issued.
        /// </summary>
        internal bool TryRequestTeardown()
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!_terminalCollected || _teardownRequested)
                {
                    return false;
                }

                if (!_outputWorker.TryRequestTeardown())
                {
                    return false;
                }

                _teardownRequested = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Normal Main Thread NV12 Texture teardown completion: true only
        /// after <see cref="TryCompleteMainThreadTextureTeardown"/> has
        /// verified the exact receipt and published the completion.
        /// </summary>
        internal bool MainThreadTextureTeardownCompleted => _mainThreadTextureTeardownCompleted;

        /// <summary>
        /// Non-waiting, exactly-once Main Thread NV12 Texture teardown. It is
        /// admitted only while the process is Draining (not Poisoned), the
        /// terminal outcome is collected, the Output Worker teardown request
        /// is accepted, the exact Submit Worker reports
        /// <c>DrainCompleted &amp;&amp; IsStopped</c>, the exact Output Worker
        /// reports <c>TeardownCompleted &amp;&amp; IsStopped</c>, and the Main
        /// Thread teardown has not run yet. A not-ready condition returns
        /// false with no side effect, for retry on the next explicit progress
        /// call; a completed teardown returns true idempotently without
        /// re-running the destroy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Main Thread teardown runs while the shared process-state gate
        /// is held, so a concurrent Poison either linearizes first (the
        /// non-waiting gate acquisition above fails, returning false with no
        /// side effect) or waits behind this completion and linearizes only
        /// after the completion is published and the gate is released.
        /// </para>
        /// </remarks>
        internal bool TryCompleteMainThreadTextureTeardown()
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // Idempotent: an already-completed teardown returns the same
                // result with no second destroy.
                if (_mainThreadTextureTeardownCompleted)
                {
                    return true;
                }

                if (!_processState.IsDraining || !_terminalCollected || !_teardownRequested)
                {
                    return false;
                }

                if (!_submitWorker.DrainCompleted || !_submitWorker.IsStopped)
                {
                    return false;
                }

                if (!_outputWorker.TeardownCompleted || !_outputWorker.IsStopped)
                {
                    return false;
                }

                // Run the exact Main Thread teardown inside the gate: a
                // concurrent Poison blocks until this completion is published
                // and the gate is released.
                NvencMainThreadTextureTeardownReceipt receipt;
                try
                {
                    receipt = _mainThreadTextureTeardown.TearDown();
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                if (receipt == null || !receipt.IsIssuedFor(_mainThreadTextureTeardown, _context))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Main Thread texture teardown returned a null or foreign receipt.");
                }

                _mainThreadTextureTeardownCompleted = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }
    }
}
