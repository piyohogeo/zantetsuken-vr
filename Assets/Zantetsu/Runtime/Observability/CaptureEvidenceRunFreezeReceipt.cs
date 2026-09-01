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
        internal long TestRunId => _runSession.TestRunId;
        internal string RunInitializationId => _runSession.RunInitializationId;
        internal bool IsValid => CorrelationsHold();

        private bool CorrelationsHold()
        {
            if (_issuedBy == null || _evidence == null || _runSession == null || _terminalBuffer == null)
            {
                return false;
            }

            if (!_runSession.IsCreated)
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
