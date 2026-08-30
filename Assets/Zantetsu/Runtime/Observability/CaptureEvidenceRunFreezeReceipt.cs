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
            return _issuedBy != null
                && _evidence != null
                && _runSession != null
                && _terminalBuffer != null
                && _runSession.IsCreated
                && _evidence.IsFullyDrained
                && _evidence.Artifacts.ReservedArtifactCount == 0
                && _evidence.Drafts.Run.TestRunId == _runSession.TestRunId
                && _terminalBuffer.TestRunId == _runSession.TestRunId
                && _issuedBy.IsFrozenFor(_runSession.TestRunId);
        }
    }
}
