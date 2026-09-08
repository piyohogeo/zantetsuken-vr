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
        /// <summary>
        /// Private-gated proof that the Main Thread NV12 Texture teardown
        /// completed normally. The type is visible to the Backend Join for
        /// exact-type checking, but it can only be minted by the Run
        /// Coordinator — at its normal return from
        /// <see cref="TryCompleteMainThreadTextureTeardown"/> — because the
        /// constructor demands a mint token whose type is private to the Run
        /// Coordinator.
        /// </summary>
        internal sealed class BackendJoinProof
        {
            internal BackendJoinProof(object token)
            {
                if (token == null || token.GetType() != typeof(TextureTeardownMintToken))
                {
                    throw new ArgumentException(
                        "The Backend Join proof can only be minted by the Run Coordinator.", nameof(token));
                }
            }
        }

        /// <summary>
        /// Mint capability held only by the Run Coordinator: the type is
        /// private, so only the Run Coordinator can construct it and thereby
        /// mint a <see cref="BackendJoinProof"/>.
        /// </summary>
        private sealed class TextureTeardownMintToken
        {
        }

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOrderedSubmitWorkerService _submitWorker;
        private readonly NvencOrderedOutputWorkerService _outputWorker;
        private readonly NvencRunChunkContext _context;
        private readonly NvencRunLocalRegistrySlot _registrySlot;
        private readonly INvencMainThreadTextureTeardown _mainThreadTextureTeardown;
        private readonly NvencCaptureBackendJoinCoordinator _backendJoin;
        private readonly CaptureRunInitializationSessionIssue _sessionIssue;
        private readonly NvencTraceFreezeCoordinator _traceFreeze;
        private readonly NvencRunPublicationPlanCommitService _publicationPlanCommitService;

        private NvencRunAcceptedFrameSnapshot _snapshot;
        private int _reflectedCount;
        private bool _allSucceeded = true;
        private bool _terminalRequested;
        private bool _requestedFinalize;
        private bool _terminalCollected;
        private bool _teardownRequested;
        private bool _mainThreadTextureTeardownCompleted;
        private NvencMainThreadTextureTeardownReceipt _mainThreadTextureTeardownReceipt;
        private bool _backendJoined;
        private BackendJoinProof _backendJoinProof;
        private NvencRunEvidenceDisposition _disposition;
        private NvencTraceFreezeReceipt _traceFreezeReceipt;
        private NvencRunPublicationPlanCommitOperation _publicationPlanCommitOperation;
        private bool _publicationPlanCommitSubmitted;
        private bool _publicationPlanCommitCollected;
        private NvencRunPublicationPlanCommitExecutionResult _publicationPlanCommitResult;

        internal NvencCaptureRunCoordinator(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencOrderedOutputWorkerService outputWorker,
            NvencRunChunkContext context,
            NvencRunLocalRegistrySlot registrySlot,
            INvencMainThreadTextureTeardown mainThreadTextureTeardown,
            NvencCaptureBackendJoinCoordinator backendJoin,
            CaptureRunInitializationSessionIssue sessionIssue,
            NvencTraceFreezeCoordinator traceFreeze,
            NvencRunPublicationPlanCommitService publicationPlanCommitService = null)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _submitWorker = submitWorker ?? throw new ArgumentNullException(nameof(submitWorker));
            _outputWorker = outputWorker ?? throw new ArgumentNullException(nameof(outputWorker));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _registrySlot = registrySlot ?? throw new ArgumentNullException(nameof(registrySlot));
            _mainThreadTextureTeardown = mainThreadTextureTeardown ?? throw new ArgumentNullException(nameof(mainThreadTextureTeardown));
            _backendJoin = backendJoin ?? throw new ArgumentNullException(nameof(backendJoin));
            _sessionIssue = sessionIssue ?? throw new ArgumentNullException(nameof(sessionIssue));
            _traceFreeze = traceFreeze ?? throw new ArgumentNullException(nameof(traceFreeze));
            _publicationPlanCommitService = publicationPlanCommitService;

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

            if (!_backendJoin.IsCorrelatedWith(_processState, _submitWorker, _outputWorker, _context, _mainThreadTextureTeardown))
            {
                throw new ArgumentException(
                    "The Backend Join must be bound to the exact process state, Submit Worker, Output Worker, Run chunk context, and Main Thread texture teardown.",
                    nameof(backendJoin));
            }

            if (!_sessionIssue.IsValid)
            {
                throw new ArgumentException(
                    "The session issue must be valid and hold a live Ownership Lease.", nameof(sessionIssue));
            }

            if (!_context.IsCorrelatedWithSessionIssue(_sessionIssue))
            {
                throw new ArgumentException(
                    "The Run chunk context must be correlated to the exact session, lock identity evidence, TestRunId, and RootLayout.",
                    nameof(sessionIssue));
            }

            if (!_traceFreeze.IsCorrelatedWith(_context, _sessionIssue))
            {
                throw new ArgumentException(
                    "The Trace freeze coordinator must be bound to the exact Run chunk context and session issue.",
                    nameof(traceFreeze));
            }

            // A supplied Publication Plan Commit Service must be bound to the
            // exact process state; this is checked before any side effect so a
            // foreign Service can never submit a Plan against this Run.
            if (_publicationPlanCommitService != null
                && !_publicationPlanCommitService.IsBoundToProcessState(_processState))
            {
                throw new ArgumentException(
                    "The Publication Plan Commit Service must be bound to the exact process state.",
                    nameof(publicationPlanCommitService));
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

                // Re-verify the O(1) exact-Run binding immediately before any
                // side effect: a teardown whose Context binding was swapped
                // after construction must never destroy a foreign Run's
                // Textures. A lost binding poisons without contacting the
                // teardown.
                if (!_mainThreadTextureTeardown.IsBoundTo(_context))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Main Thread texture teardown is no longer bound to the exact Run chunk context.");
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

                _mainThreadTextureTeardownReceipt = receipt;
                _backendJoinProof = new BackendJoinProof(new TextureTeardownMintToken());
                _mainThreadTextureTeardownCompleted = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// True only after <see cref="TryCompleteBackendJoin"/> has succeeded
        /// once.
        /// </summary>
        internal bool BackendJoined => _backendJoined;

        /// <summary>
        /// Non-waiting, idempotent Backend Join entry. It is admitted only
        /// after the Main Thread NV12 Texture teardown completed, and then
        /// passes the minted private-gated Backend Join proof to the exact
        /// Backend Join boundary; on its success the join is latched. A
        /// not-ready condition or a gate contention returns false with no side
        /// effect, and the Main Thread Texture teardown boundary is never
        /// contacted before it has completed.
        /// </summary>
        internal bool TryCompleteBackendJoin()
        {
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // Idempotent: an already-completed join returns the same result.
                if (_backendJoined)
                {
                    return true;
                }

                if (!_mainThreadTextureTeardownCompleted)
                {
                    return false;
                }

                // Re-verify the O(1) exact-graph correlation immediately before
                // any side effect, so a backend join whose binding was swapped
                // after construction never joins a foreign Run.
                if (!_backendJoin.IsCorrelatedWith(_processState, _submitWorker, _outputWorker, _context, _mainThreadTextureTeardown))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Backend Join is no longer bound to the exact Run graph.");
                }

                if (!_backendJoin.TryJoin(_backendJoinProof))
                {
                    return false;
                }

                _backendJoined = true;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// The append-only evidence disposition published once by
        /// <see cref="TryCompleteTraceFreeze"/>. <see cref="NvencRunEvidenceDisposition.None"/>
        /// until a disposition is published.
        /// </summary>
        internal NvencRunEvidenceDisposition Disposition => _disposition;

        /// <summary>
        /// Non-waiting, idempotent NVENC Trace freeze and disposition boundary.
        /// It contacts the Trace only when the process is Draining and not
        /// Poisoned, the terminal outcome is collected, the Main Thread Texture
        /// teardown completed, the Backend Join succeeded, the Ownership Lease
        /// is still live, the disposition is still <see cref="NvencRunEvidenceDisposition.None"/>,
        /// and the exact Trace graph correlation holds. A Finalized registered
        /// entry publishes <see cref="NvencRunEvidenceDisposition.Finalized"/>;
        /// an Abandoned empty slot publishes
        /// <see cref="NvencRunEvidenceDisposition.Incomplete"/>. The Registry
        /// slot is not advanced to Committed and the Ownership Lease is not
        /// released here. A seal or terminal-append exception propagates
        /// unchanged without poisoning and without publishing a disposition, so
        /// the caller can retry through the retained seal or abort explicitly;
        /// only a broken correlation or a corrupted freeze receipt poisons.
        /// </summary>
        internal bool TryCompleteTraceFreeze(
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint,
            out NvencTraceFreezeReceipt receipt)
        {
            receipt = null;
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // Idempotent: an already-published disposition returns the same
                // receipt without re-sealing or re-appending the trace, but
                // only while the retained receipt's full correlation still
                // holds. A nulled, corrupted, or lease-released retained
                // receipt fails closed instead of re-reporting success.
                if (_disposition != NvencRunEvidenceDisposition.None)
                {
                    if (_traceFreezeReceipt == null || !_traceFreezeReceipt.IsValid
                        || !_traceFreezeReceipt.IsIssuedFor(_traceFreeze, _context, _sessionIssue))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The retained Trace freeze receipt is null, foreign, or corrupted.");
                    }

                    receipt = _traceFreezeReceipt;
                    return true;
                }

                if (!_processState.IsDraining || _processState.IsPoisoned)
                {
                    return false;
                }

                if (!_terminalCollected)
                {
                    return false;
                }

                if (!_mainThreadTextureTeardownCompleted)
                {
                    return false;
                }

                if (!_backendJoined)
                {
                    return false;
                }

                if (!_sessionIssue.IsValid)
                {
                    return false;
                }

                // Re-verify the O(1) exact-graph correlation immediately before
                // any side effect, so a trace freeze whose binding was swapped
                // after construction never freezes a foreign Run.
                if (!_traceFreeze.IsCorrelatedWith(_context, _sessionIssue))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Trace freeze is no longer bound to the exact Run graph.");
                }

                bool finalized;
                if (_context.State == NvencRunChunkContextState.Finalized
                    && _registrySlot.State == NvencRunLocalRegistrySlotState.Registered
                    && _registrySlot.TryGetEntry(out _, out _, out _))
                {
                    finalized = true;
                }
                else if (_context.State == NvencRunChunkContextState.Abandoned
                    && _registrySlot.State == NvencRunLocalRegistrySlotState.Empty)
                {
                    finalized = false;
                }
                else
                {
                    // Readiness without a recognized final or abandoned shape:
                    // no Trace contact and no disposition.
                    return false;
                }

                NvencTraceFreezeReceipt freezeReceipt;
                if (!_traceFreeze.TryCompleteFreeze(forcedDropFrameIds, checkpoint, out freezeReceipt))
                {
                    return false;
                }

                if (freezeReceipt == null || !freezeReceipt.IsValid
                    || !freezeReceipt.IsIssuedFor(_traceFreeze, _context, _sessionIssue))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "Trace freeze returned a null, foreign, or corrupted receipt.");
                }

                _traceFreezeReceipt = freezeReceipt;
                _disposition = finalized
                    ? NvencRunEvidenceDisposition.Finalized
                    : NvencRunEvidenceDisposition.Incomplete;
                receipt = freezeReceipt;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Non-waiting, idempotent Publication Plan commit preparation. It is
        /// admitted only after the Trace freeze published
        /// <see cref="NvencRunEvidenceDisposition.Finalized"/>, the Backend
        /// Join and Main Thread Texture teardown completed, the Ownership
        /// Lease is still live, the context is Finalized, the Registry Slot is
        /// Registered with an intact entry that exactly correlates to the
        /// context's held finalization result, and the retained Trace freeze
        /// receipt is still valid and exact. Only then is the plan built and
        /// the operation minted and retained.
        /// </summary>
        /// <remarks>
        /// A same manifest hash re-call returns the exact retained operation
        /// reference; a different hash is rejected before any side effect. The
        /// disposition, Registry Slot, Ownership Lease, Trace, chunk file, and
        /// context terminal state are never changed here. An unexpected
        /// corruption in the retained receipt, entry, plan, or operation
        /// poisons and throws; a mere not-ready shape or a gate contention
        /// returns false without change.
        /// </remarks>
        internal bool TryPreparePublicationPlanCommit(
            string runManifestContentHash,
            out NvencRunPublicationPlanCommitOperation operation)
        {
            operation = null;

            if (runManifestContentHash == null)
            {
                throw new ArgumentNullException(nameof(runManifestContentHash));
            }

            if (!IsLowerHex(runManifestContentHash, 64))
            {
                throw new ArgumentException(
                    "Manifest hash must be 64 lowercase hex characters.", nameof(runManifestContentHash));
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_publicationPlanCommitOperation != null)
                {
                    if (!string.Equals(
                            _publicationPlanCommitOperation.RunManifestContentHash,
                            runManifestContentHash,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (!_publicationPlanCommitOperation.IsValid
                        || !_publicationPlanCommitOperation.IsIssuedFor(this))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The retained publication plan commit operation is no longer valid.");
                    }

                    operation = _publicationPlanCommitOperation;
                    return true;
                }

                if (!_processState.IsDraining || _processState.IsPoisoned)
                {
                    return false;
                }

                if (_disposition != NvencRunEvidenceDisposition.Finalized)
                {
                    return false;
                }

                if (!_terminalCollected || !_mainThreadTextureTeardownCompleted || !_backendJoined)
                {
                    return false;
                }

                // A released Ownership Lease is a not-ready shape, not a
                // corruption: refuse without poison before inspecting the
                // Trace freeze receipt, whose validity also depends on the
                // still-live lease.
                if (!_sessionIssue.IsValid)
                {
                    return false;
                }

                if (_traceFreezeReceipt == null || !_traceFreezeReceipt.IsValid
                    || !_traceFreezeReceipt.IsIssuedFor(_traceFreeze, _context, _sessionIssue))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The retained Trace freeze receipt is null, foreign, or corrupted.");
                }

                if (_context.State != NvencRunChunkContextState.Finalized)
                {
                    return false;
                }

                if (_registrySlot.State != NvencRunLocalRegistrySlotState.Registered)
                {
                    return false;
                }

                if (!_registrySlot.TryGetEntry(out NvencChunkFinalizationResult entryResult, out _, out _))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The Registry Slot registered entry no longer correlates.");
                }

                if (!_context.TryGetFinalizationResult(out NvencChunkFinalizationResult held)
                    || !ReferenceEquals(held, entryResult))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The Registry Slot entry does not correlate to the context's held finalization result.");
                }

                CapturePublicationPlan plan;
                try
                {
                    plan = NvencRunPublicationPlanBuilder.Build(
                        entryResult, _sessionIssue, runManifestContentHash);
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                NvencRunPublicationPlanCommitOperation minted;
                try
                {
                    minted = new NvencRunPublicationPlanCommitOperation(
                        this, _traceFreezeReceipt, entryResult, plan);
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                // Retain the minted operation before the issuance re-check, so
                // the exact retained identity is part of the predicate.
                _publicationPlanCommitOperation = minted;

                if (!minted.IsValid || !minted.IsIssuedFor(this))
                {
                    _publicationPlanCommitOperation = null;
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The publication plan commit operation does not correlate after construction.");
                }

                operation = minted;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        /// <summary>
        /// Exception-safe exact-issuance re-verification used by the retained
        /// <see cref="NvencRunPublicationPlanCommitOperation"/>: the exact
        /// retained Trace freeze receipt, a still-<see cref="NvencRunEvidenceDisposition.Finalized"/>
        /// disposition, a Finalized context, a Registered Registry Slot whose
        /// exact entry result, descriptor, and relation match the supplied
        /// values, the context's held finalization result correlation, a live
        /// Session Ownership Lease, and the exact one-artifact, full-frame
        /// plan reduction. It performs no side effect and never throws.
        /// </summary>
        internal bool IsPublicationPlanCommitIssued(
            NvencTraceFreezeReceipt traceFreezeReceipt,
            NvencChunkFinalizationResult finalizationResult,
            CapturePublicationPlan plan)
        {
            try
            {
                if (traceFreezeReceipt == null || finalizationResult == null || plan == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_traceFreezeReceipt, traceFreezeReceipt))
                {
                    return false;
                }

                if (_disposition != NvencRunEvidenceDisposition.Finalized)
                {
                    return false;
                }

                if (_context.State != NvencRunChunkContextState.Finalized)
                {
                    return false;
                }

                if (_registrySlot.State != NvencRunLocalRegistrySlotState.Registered)
                {
                    return false;
                }

                if (!_registrySlot.TryGetEntry(
                        out NvencChunkFinalizationResult entryResult,
                        out CaptureArtifactDescriptor entryDescriptor,
                        out CaptureArtifactFrameRelation entryRelation))
                {
                    return false;
                }

                if (!ReferenceEquals(entryResult, finalizationResult)
                    || !ReferenceEquals(entryDescriptor, finalizationResult.Descriptor)
                    || !ReferenceEquals(entryRelation, finalizationResult.FrameRelation))
                {
                    return false;
                }

                if (!_context.TryGetFinalizationResult(out NvencChunkFinalizationResult held)
                    || !ReferenceEquals(held, entryResult))
                {
                    return false;
                }

                if (!_sessionIssue.IsValid)
                {
                    return false;
                }

                if (!traceFreezeReceipt.IsValid
                    || !traceFreezeReceipt.IsIssuedFor(_traceFreeze, _context, _sessionIssue))
                {
                    return false;
                }

                if (plan.TestRunId != _context.TestRunId
                    || !string.Equals(
                        plan.RunInitializationId,
                        _sessionIssue.Session.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (plan.ArtifactCount != 1
                    || !ReferenceEquals(plan.GetArtifact(0), entryDescriptor))
                {
                    return false;
                }

                if (plan.CaptureFrameEvidenceCount != entryRelation.Count)
                {
                    return false;
                }

                for (int i = 0; i < entryRelation.Count; i++)
                {
                    CaptureFrameEvidenceEntry entry = plan.GetCaptureFrameEvidence(i);
                    if (entry == null
                        || entry.CaptureFrameId != entryRelation.GetCaptureFrameId(i)
                        || entry.ArtifactCount != 1
                        || !string.Equals(entry.GetArtifactId(0), entryDescriptor.ArtifactId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Exception-safe exact-retention predicate: true only when the given
        /// operation is the exact operation this coordinator currently holds
        /// from its <see cref="TryPreparePublicationPlanCommit"/> issuance. A
        /// directly reconstructed or manifest-hash-substituted operation is
        /// therefore never treated as issued. It performs no side effect and
        /// never throws.
        /// </summary>
        internal bool IsRetainedPublicationPlanCommitOperation(
            NvencRunPublicationPlanCommitOperation operation)
        {
            return operation != null
                && ReferenceEquals(_publicationPlanCommitOperation, operation);
        }

        /// <summary>
        /// Minimal O(1) exact-process-state correlation used by the Publication
        /// Service: true only when the supplied operation is the exact retained
        /// publication plan commit operation and this Run Coordinator is bound
        /// to the exact supplied process state. ReferenceEquals only, no side
        /// effect, and neither the process state nor the retained operation is
        /// exposed as a property.
        /// </summary>
        internal bool IsPublicationPlanCommitOperationBoundTo(
            NvencRunPublicationPlanCommitOperation operation,
            NvencCaptureProcessState processState)
        {
            return operation != null
                && processState != null
                && ReferenceEquals(_publicationPlanCommitOperation, operation)
                && ReferenceEquals(_processState, processState);
        }

        /// <summary>
        /// Exception-safe post-commit issuance binding predicate: the exact
        /// retained operation identity plus the exact Trace freeze receipt,
        /// the exact finalization result, the exact one-artifact full-frame
        /// plan, the run identity, and a live Session Issue. It deliberately
        /// does not inspect the disposition or the Registry Slot state, so
        /// advancing the slot to <c>Committed</c> does not invalidate an
        /// issued receipt or Committed result. A corrupted plan, descriptor,
        /// relation, Trace receipt, or Session Issue converges to false.
        /// </summary>
        internal bool IsPublicationPlanCommitBindingIntact(
            NvencRunPublicationPlanCommitOperation operation)
        {
            try
            {
                if (operation == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_publicationPlanCommitOperation, operation))
                {
                    return false;
                }

                NvencTraceFreezeReceipt receipt = operation.TraceFreezeReceipt;
                NvencChunkFinalizationResult result = operation.FinalizationResult;
                CapturePublicationPlan plan = operation.Plan;

                if (receipt == null || result == null || plan == null)
                {
                    return false;
                }

                if (!plan.IsValid)
                {
                    return false;
                }

                if (!ReferenceEquals(_traceFreezeReceipt, receipt))
                {
                    return false;
                }

                if (!_sessionIssue.IsValid)
                {
                    return false;
                }

                if (!receipt.IsValid || !receipt.IsIssuedFor(_traceFreeze, _context, _sessionIssue))
                {
                    return false;
                }

                if (!result.IsValid)
                {
                    return false;
                }

                // Bind the finalization result to the exact Run chunk context:
                // the result's sink must be the context's sink, and the context
                // must hold the exact result reference.
                if (result.Sink == null || !ReferenceEquals(result.Sink, _context.Sink))
                {
                    return false;
                }

                if (!_context.TryGetFinalizationResult(out NvencChunkFinalizationResult held)
                    || !ReferenceEquals(held, result))
                {
                    return false;
                }

                CaptureArtifactDescriptor descriptor = result.Descriptor;
                if (descriptor == null || !descriptor.IsValid
                    || descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
                {
                    return false;
                }

                CaptureArtifactFrameRelation relation = result.FrameRelation;
                if (relation == null || !relation.IsValid
                    || relation.Count < 1 || relation.Count > NvencBringUpProfileV1.CadenceTickCount)
                {
                    return false;
                }

                if (plan.TestRunId != _context.TestRunId
                    || !string.Equals(
                        plan.RunInitializationId,
                        _sessionIssue.Session.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (plan.ArtifactCount != 1
                    || !ReferenceEquals(plan.GetArtifact(0), descriptor))
                {
                    return false;
                }

                if (plan.CaptureFrameEvidenceCount != relation.Count)
                {
                    return false;
                }

                for (int i = 0; i < relation.Count; i++)
                {
                    CaptureFrameEvidenceEntry entry = plan.GetCaptureFrameEvidence(i);
                    if (entry == null
                        || entry.CaptureFrameId != relation.GetCaptureFrameId(i)
                        || entry.ArtifactCount != 1
                        || !string.Equals(entry.GetArtifactId(0), descriptor.ArtifactId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Non-waiting, idempotent submission of the retained publication plan
        /// commit operation to the exact Publication Plan Commit Service. It is
        /// admitted only when the process is not Poisoned, the disposition is
        /// <see cref="NvencRunEvidenceDisposition.Finalized"/>, the retained
        /// operation exists and is still valid, the context is Finalized, the
        /// Registry Slot is Registered, the Trace Freeze, Session Issue, and
        /// Backend Join correlations still hold, and the Service is still
        /// accepting. The submission is linearized with the Poison transition
        /// on the shared process-state gate; on acceptance the retained
        /// operation is handed to the Service exactly once. A second submission,
        /// a submission before preparation, a gate contention, or a poisoned
        /// process returns false with no change.
        /// </summary>
        internal bool TrySubmitPublicationPlanCommit()
        {
            if (_publicationPlanCommitService == null)
            {
                return false;
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    return false;
                }

                if (_publicationPlanCommitSubmitted)
                {
                    return false;
                }

                if (_disposition != NvencRunEvidenceDisposition.Finalized)
                {
                    return false;
                }

                NvencRunPublicationPlanCommitOperation operation = _publicationPlanCommitOperation;
                if (operation == null
                    || !operation.IsValid
                    || !operation.IsIssuedFor(this))
                {
                    return false;
                }

                if (_context.State != NvencRunChunkContextState.Finalized)
                {
                    return false;
                }

                if (_registrySlot.State != NvencRunLocalRegistrySlotState.Registered)
                {
                    return false;
                }

                if (!IsPublicationPlanCommitIssued(
                        operation.TraceFreezeReceipt,
                        operation.FinalizationResult,
                        operation.Plan))
                {
                    return false;
                }

                if (_publicationPlanCommitService.State
                    != NvencRunPublicationPlanCommitServiceState.Accepting)
                {
                    return false;
                }

                // The Service re-checks its own state inside the same
                // process-state gate (reentrant), so the retained operation is
                // handed over exactly once.
                if (!_publicationPlanCommitService.TrySubmit(operation))
                {
                    return false;
                }

                _publicationPlanCommitSubmitted = true;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-waiting, idempotent collection and reflection of the publication
        /// plan commit outcome into the Run's authoritative state. The first
        /// successful call collects the Execution Result from the Service
        /// exactly once, verifies it against the retained operation and the Run
        /// identity, releases the Service wait handle exactly once, advances the
        /// Registry Slot per status, retains the result, and publishes the
        /// disposition last. Re-calls return the same retained reference after
        /// re-checking the current correlation without re-collecting,
        /// re-transitioning, or re-disposing. A null, foreign, or corrupt
        /// result, a Service fatal failure, or a failed Registry transition
        /// poisons without guessing another disposition. An external Poison
        /// that linearized first never reflects a normal result.
        /// </summary>
        internal bool TryCollectPublicationPlanCommit(
            out NvencRunPublicationPlanCommitExecutionResult result)
        {
            result = null;

            if (_publicationPlanCommitService == null)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (_publicationPlanCommitCollected)
                {
                    NvencRunPublicationPlanCommitExecutionResult retained = _publicationPlanCommitResult;
                    if (retained != null && IsRetainedCommitResultCorrelated(retained))
                    {
                        result = retained;
                        return true;
                    }

                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The retained publication plan commit result no longer correlates.");
                }

                if (_processState.IsPoisoned)
                {
                    return false;
                }

                if (!_publicationPlanCommitService.TryCollect(
                        out NvencRunPublicationPlanCommitExecutionResult collected))
                {
                    return false;
                }

                if (collected == null
                    || !collected.IsValid
                    || !IsCollectedCommitResultCorrelated(collected))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The publication plan commit result is null, foreign, or corrupt.");
                }

                if (_publicationPlanCommitService.TryGetFailure(out _))
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The publication plan commit service reported a fatal failure.");
                }

                if (!_publicationPlanCommitService.IsStopped)
                {
                    return false;
                }

                // Release the Service wait handle exactly once. A dispose
                // failure poisons and propagates the original exception without
                // faking a successful state.
                try
                {
                    _publicationPlanCommitService.Dispose();
                }
                catch (Exception ex)
                {
                    _processState.TryPoison();
                    throw;
                }

                ReflectPublicationPlanCommit(collected);

                _publicationPlanCommitCollected = true;
                result = collected;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool IsCollectedCommitResultCorrelated(
            NvencRunPublicationPlanCommitExecutionResult collected)
        {
            NvencRunPublicationPlanCommitOperation operation = _publicationPlanCommitOperation;

            return operation != null
                && ReferenceEquals(collected.Attempt.Operation, operation)
                && ReferenceEquals(collected.Plan, operation.Plan)
                && ReferenceEquals(collected.FinalizationResult, operation.FinalizationResult)
                && ReferenceEquals(collected.TraceFreezeReceipt, operation.TraceFreezeReceipt)
                && collected.TestRunId == _context.TestRunId
                && string.Equals(
                    collected.RunInitializationId,
                    _sessionIssue.Session.RunInitializationId,
                    StringComparison.Ordinal);
        }

        private bool IsRetainedCommitResultCorrelated(
            NvencRunPublicationPlanCommitExecutionResult retained)
        {
            NvencRunPublicationPlanCommitOperation operation = _publicationPlanCommitOperation;

            return retained.IsValid
                && operation != null
                && ReferenceEquals(retained.Attempt.Operation, operation)
                && ReferenceEquals(retained.Plan, operation.Plan)
                && ReferenceEquals(retained.FinalizationResult, operation.FinalizationResult);
        }

        /// <summary>
        /// Advances the Run's authoritative state from the exact committed
        /// status: the Registry transition and the retained result are fixed
        /// first, and the disposition is published last. No Plan, chunk, or
        /// dedicated tmp file is touched or inspected here, and a failed
        /// Registry transition poisons without guessing another disposition.
        /// </summary>
        private void ReflectPublicationPlanCommit(
            NvencRunPublicationPlanCommitExecutionResult collected)
        {
            NvencRunPublicationPlanCommitOperation operation = _publicationPlanCommitOperation;
            NvencChunkFinalizationResult entryResult = operation.FinalizationResult;
            NvencRunEvidenceDisposition next;

            switch (collected.Status)
            {
                case NvencRunPublicationPlanCommitStatus.Committed:
                {
                    NvencRunPublicationPlanCommitReceipt receipt = collected.Receipt;
                    if (receipt == null
                        || !receipt.IsIssuedFor(collected.Attempt.Committer, operation))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The Committed result does not carry an exact valid receipt.");
                    }

                    if (!_registrySlot.TryCommit(_context, entryResult))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The Registry Slot could not advance to Committed.");
                    }

                    next = NvencRunEvidenceDisposition.Committed;
                    break;
                }

                case NvencRunPublicationPlanCommitStatus.FailedBeforeRename:
                {
                    if (!_registrySlot.TryDiscardRegistered(_context, entryResult))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The Registry Slot could not discard the registered entry.");
                    }

                    next = NvencRunEvidenceDisposition.Incomplete;
                    break;
                }

                case NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown:
                {
                    // The Registry Slot stays Registered; the commit outcome is
                    // never re-inspected or guessed, and nothing is discarded or
                    // cleaned up.
                    if (_registrySlot.State != NvencRunLocalRegistrySlotState.Registered)
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "The Registry Slot is no longer Registered for an unknown outcome.");
                    }

                    next = NvencRunEvidenceDisposition.CommitOutcomeUnknown;
                    break;
                }

                default:
                {
                    _processState.TryPoison();
                    throw new InvalidOperationException(
                        "The publication plan commit result has an unrecognized status.");
                }
            }

            // Retain the result before the disposition becomes observable.
            _publicationPlanCommitResult = collected;

            // Publish the disposition last.
            _disposition = next;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
