using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-owning proof that the existing freeze coordinator stopped and
    /// joined evidence processing, drained reservations, froze Trace, and did
    /// so while the correlated Run session still held the OS lock.
    /// </summary>
    internal sealed class CaptureEvidenceRunFreezeReceipt
    {
        private readonly CaptureFrameFreezeTerminalCoordinator _issuedBy;
        private readonly CaptureEvidenceDraftCoordinator _evidence;
        private readonly CaptureRunInitializationSession _runSession;
        private readonly FreezeTerminalTraceBuffer _terminalBuffer;

        internal CaptureEvidenceRunFreezeReceipt(
            CaptureFrameFreezeTerminalCoordinator issuedBy,
            CaptureEvidenceDraftCoordinator evidence,
            CaptureRunInitializationSession runSession,
            FreezeTerminalTraceBuffer terminalBuffer)
        {
            _issuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
            _terminalBuffer = terminalBuffer ?? throw new ArgumentNullException(nameof(terminalBuffer));
            if (!CorrelationsHold()) throw new ArgumentException("Freeze evidence is not fully correlated.", nameof(evidence));
        }

        internal CaptureFrameFreezeTerminalCoordinator IssuedBy => _issuedBy;
        internal CaptureFrameDraftRegistry Drafts => _evidence.Drafts;
        internal CaptureArtifactRegistry Artifacts => _evidence.Artifacts;
        internal CaptureRunInitializationSession RunSession => _runSession;
        internal FreezeTerminalTraceBuffer TerminalBuffer => _terminalBuffer;
        internal CaptureRunRootLayout RootLayout => _runSession.RootLayout;
        internal CaptureRunLockLease LockLease => _runSession.LockLease;
        internal long TestRunId => _runSession.TestRunId;
        internal string RunInitializationId => _runSession.RunInitializationId;
        internal bool IsValid => CorrelationsHold();

        /// <summary>
        /// O(1) exception-safe structural guard for proof matching: safely
        /// reads the current drafts, artifacts, session, and lock lease without
        /// throwing when the freeze receipt's evidence or session references
        /// have been nulled after issuance. Returns <c>false</c> for any
        /// corrupted reference.
        /// </summary>
        internal bool TryGetIssuedBindings(
            out CaptureFrameDraftRegistry drafts,
            out CaptureArtifactRegistry artifacts,
            out CaptureRunInitializationSession session,
            out CaptureRunLockLease lockLease)
        {
            drafts = null;
            artifacts = null;
            session = null;
            lockLease = null;

            CaptureEvidenceDraftCoordinator evidence = _evidence;
            CaptureRunInitializationSession runSession = _runSession;
            if (evidence == null || runSession == null)
            {
                return false;
            }

            CaptureFrameDraftRegistry d = evidence.Drafts;
            CaptureArtifactRegistry a = evidence.Artifacts;
            if (d == null || a == null)
            {
                return false;
            }

            CaptureRunLockLease lease = runSession.LockLease;
            if (lease == null)
            {
                return false;
            }

            drafts = d;
            artifacts = a;
            session = runSession;
            lockLease = lease;
            return true;
        }

        private bool CorrelationsHold()
        {
            if (_issuedBy == null || _evidence == null || _runSession == null || _terminalBuffer == null)
            {
                return false;
            }

            if (!_runSession.IsLockOwnershipIntact)
            {
                return false;
            }

            CaptureFrameDraftRegistry drafts = _evidence.Drafts;
            CaptureArtifactRegistry artifacts = _evidence.Artifacts;
            if (drafts == null || artifacts == null || drafts.Run == null)
            {
                return false;
            }

            if (!_evidence.IsFullyDrained)
            {
                return false;
            }

            if (artifacts.ReservedArtifactCount != 0)
            {
                return false;
            }

            if (drafts.Run.TestRunId != _runSession.TestRunId)
            {
                return false;
            }

            if (_terminalBuffer.TestRunId != _runSession.TestRunId)
            {
                return false;
            }

            if (!_issuedBy.IsFrozenFor(_runSession.TestRunId))
            {
                return false;
            }

            return true;
        }
    }
}
