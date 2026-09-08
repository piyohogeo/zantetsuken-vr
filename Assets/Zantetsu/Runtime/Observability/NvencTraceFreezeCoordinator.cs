using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// NVENC-only narrow Trace freeze boundary for one Phase 0.11 Run. It
    /// reuses the existing <see cref="TraceLogger.SealAndDrainRunForFreeze"/>,
    /// <see cref="CaptureFrameFreezeTerminalCoordinator.Complete"/>, and
    /// <see cref="TraceFlightRecorder"/> Frozen transition, and issues an
    /// exact-correlated <see cref="NvencTraceFreezeReceipt"/> once Frozen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The forced-drop frame ID set and the freeze terminal checkpoint are
    /// caller-determined inputs: this coordinator never samples the clock,
    /// frame ID, or thread ID itself. It performs no evidence drain or join —
    /// the NVENC Backend Join already completed — and only seals the trace,
    /// appends the freeze terminal buffer, verifies Frozen, and issues the
    /// receipt.
    /// </para>
    /// <para>
    /// A successful freeze is idempotent: the issued receipt is retained and a
    /// later call returns the same receipt without re-sealing or re-appending.
    /// A seal or append exception is propagated unchanged and no disposition is
    /// published. This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencTraceFreezeCoordinator
    {
        private readonly TraceLogger _logger;
        private readonly TraceFlightRecorder _recorder;
        private readonly CaptureFrameFreezeTerminalCoordinator _freezeTerminalCoordinator;
        private readonly NvencRunChunkContext _context;
        private readonly CaptureRunInitializationSessionIssue _sessionIssue;

        private NvencTraceFreezeReceipt _issuedReceipt;

        internal NvencTraceFreezeCoordinator(
            TraceLogger logger,
            TraceFlightRecorder recorder,
            CaptureFrameFreezeTerminalCoordinator freezeTerminalCoordinator,
            NvencRunChunkContext context,
            CaptureRunInitializationSessionIssue sessionIssue)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _freezeTerminalCoordinator = freezeTerminalCoordinator ?? throw new ArgumentNullException(nameof(freezeTerminalCoordinator));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _sessionIssue = sessionIssue ?? throw new ArgumentNullException(nameof(sessionIssue));

            if (!_logger.IsCaptureRun)
            {
                throw new ArgumentException("The trace freeze logger must be a capture-run logger.", nameof(logger));
            }

            if (!ReferenceEquals(_recorder.Logger, _logger))
            {
                throw new ArgumentException("The recorder must reference the exact logger.", nameof(recorder));
            }

            if (_recorder.FreezeTerminalTraceReserve <= 0)
            {
                throw new ArgumentException("The recorder must be reserve-configured for the freeze terminal append.", nameof(recorder));
            }

            if (!_context.IsCorrelatedWithSessionIssue(_sessionIssue))
            {
                throw new ArgumentException(
                    "The Run chunk context must be correlated to the exact session issue.", nameof(context));
            }

            if (_logger.TestRunId <= 0 || _logger.TestRunId != _context.TestRunId)
            {
                throw new ArgumentException(
                    "The trace freeze logger must be bound to the exact Run chunk context test run ID.", nameof(logger));
            }
        }

        /// <summary>
        /// O(1) exact-reference correlation predicate: true only when this
        /// freeze coordinator is bound to the exact Run chunk context and the
        /// exact session issue, without exposing the logger or recorder.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencRunChunkContext context,
            CaptureRunInitializationSessionIssue sessionIssue)
        {
            return ReferenceEquals(_context, context)
                && ReferenceEquals(_sessionIssue, sessionIssue);
        }

        /// <summary>
        /// Seals and drains the run trace, completes the freeze terminal
        /// append, verifies <see cref="TraceFlightRecorderState.Frozen"/>, and
        /// issues the exact-correlated receipt. Idempotent after success: a
        /// later call returns the same receipt without re-sealing or
        /// re-appending. A seal or append exception is propagated unchanged.
        /// </summary>
        internal bool TryCompleteFreeze(
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint,
            out NvencTraceFreezeReceipt receipt)
        {
            if (_issuedReceipt != null)
            {
                receipt = _issuedReceipt;
                return true;
            }

            receipt = null;

            TraceRunSealReceipt sealReceipt = _logger.SealAndDrainRunForFreeze(_logger.TestRunId, _recorder);
            FreezeTerminalTraceBuffer terminalBuffer = _freezeTerminalCoordinator.Complete(
                sealReceipt, forcedDropFrameIds, checkpoint, true);

            if (_recorder.State != TraceFlightRecorderState.Frozen)
            {
                throw new InvalidOperationException("The trace recorder did not reach Frozen after the freeze terminal append.");
            }

            NvencTraceFreezeReceipt issued = new NvencTraceFreezeReceipt(
                this, _context, _sessionIssue, sealReceipt, terminalBuffer);
            _issuedReceipt = issued;
            receipt = issued;
            return true;
        }
    }
}
