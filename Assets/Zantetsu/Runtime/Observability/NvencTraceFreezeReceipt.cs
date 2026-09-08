using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning proof that the NVENC Trace freeze completed: the
    /// exact freeze coordinator sealed and drained the run trace, appended the
    /// freeze terminal buffer, and reached <see cref="TraceFlightRecorderState.Frozen"/>,
    /// while the exact Run chunk context and the exact session issue — with a
    /// still-live Ownership Lease — were correlated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt holds only the exact issuer, context, session issue, seal
    /// receipt, and terminal buffer for identity and correlation verification.
    /// It holds no token, nonce, registry, ownership bundle, owned resource, or
    /// disposal capability, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencTraceFreezeReceipt
    {
        private readonly NvencTraceFreezeCoordinator _issuedBy;
        private readonly NvencRunChunkContext _context;
        private readonly CaptureRunInitializationSessionIssue _sessionIssue;
        private readonly TraceRunSealReceipt _sealReceipt;
        private readonly FreezeTerminalTraceBuffer _terminalBuffer;

        internal NvencTraceFreezeReceipt(
            NvencTraceFreezeCoordinator issuedBy,
            NvencRunChunkContext context,
            CaptureRunInitializationSessionIssue sessionIssue,
            TraceRunSealReceipt sealReceipt,
            FreezeTerminalTraceBuffer terminalBuffer)
        {
            _issuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _sessionIssue = sessionIssue ?? throw new ArgumentNullException(nameof(sessionIssue));
            _sealReceipt = sealReceipt ?? throw new ArgumentNullException(nameof(sealReceipt));
            _terminalBuffer = terminalBuffer ?? throw new ArgumentNullException(nameof(terminalBuffer));

            if (!CorrelationsHold())
            {
                throw new ArgumentException("The trace freeze proof is not fully correlated.", nameof(issuedBy));
            }
        }

        internal NvencTraceFreezeCoordinator IssuedBy => _issuedBy;

        internal NvencRunChunkContext Context => _context;

        internal CaptureRunInitializationSessionIssue SessionIssue => _sessionIssue;

        internal TraceRunSealReceipt SealReceipt => _sealReceipt;

        internal FreezeTerminalTraceBuffer TerminalBuffer => _terminalBuffer;

        internal long TestRunId => _sealReceipt != null ? _sealReceipt.TestRunId : 0L;

        /// <summary>
        /// Exception-safe re-computation of the exact correlation. A nulled,
        /// foreign, or released reference converges to <c>false</c> without
        /// throwing.
        /// </summary>
        internal bool IsValid => CorrelationsHold();

        /// <summary>
        /// Exception-safe exact-issuance check: true only for the exact freeze
        /// coordinator, the exact Run chunk context, and the exact session
        /// issue this receipt was issued for, and only while the full
        /// correlation — including the exact issued seal receipt, the exact
        /// appended terminal buffer, and the recorder's Frozen state — still
        /// holds.
        /// </summary>
        internal bool IsIssuedFor(
            NvencTraceFreezeCoordinator issuedBy,
            NvencRunChunkContext context,
            CaptureRunInitializationSessionIssue sessionIssue)
        {
            return _issuedBy != null && issuedBy != null
                && _context != null && context != null
                && _sessionIssue != null && sessionIssue != null
                && ReferenceEquals(_issuedBy, issuedBy)
                && ReferenceEquals(_context, context)
                && ReferenceEquals(_sessionIssue, sessionIssue)
                && CorrelationsHold();
        }

        private bool CorrelationsHold()
        {
            if (_issuedBy == null || _context == null || _sessionIssue == null
                || _sealReceipt == null || _terminalBuffer == null)
            {
                return false;
            }

            if (!_sessionIssue.IsValid)
            {
                return false;
            }

            if (!_context.IsCorrelatedWithSessionIssue(_sessionIssue))
            {
                return false;
            }

            if (_sealReceipt.TestRunId <= 0 || _sealReceipt.TestRunId != _context.TestRunId)
            {
                return false;
            }

            if (_terminalBuffer.TestRunId != _context.TestRunId)
            {
                return false;
            }

            // Exact binding to the actual seal and Frozen transition: the seal
            // receipt must be the exact one the coordinator issued and the
            // terminal buffer the exact one it appended, with the recorder
            // actually Frozen. A foreign seal receipt or same-Run buffer that
            // was never appended therefore fails.
            if (!_issuedBy.IsIssuedFreeze(_sealReceipt, _terminalBuffer))
            {
                return false;
            }

            return true;
        }
    }
}
