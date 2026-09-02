using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-owning proof that the existing freeze coordinator stopped and
    /// joined evidence processing, drained reservations, froze Trace, and did
    /// so while the correlated Run's lock ownership was still live, as proven
    /// by the exact lock identity evidence.
    /// </summary>
    internal sealed class CaptureEvidenceRunFreezeReceipt
    {
        private readonly CaptureFrameFreezeTerminalCoordinator _issuedBy;
        private readonly CaptureEvidenceDraftCoordinator _evidence;
        private readonly CaptureRunInitializationSession _runSession;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
        private readonly FreezeTerminalTraceBuffer _terminalBuffer;

        internal CaptureEvidenceRunFreezeReceipt(
            CaptureFrameFreezeTerminalCoordinator issuedBy,
            CaptureEvidenceDraftCoordinator evidence,
            CaptureRunInitializationSession runSession,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            FreezeTerminalTraceBuffer terminalBuffer)
        {
            _issuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
            _lockIdentityEvidence = lockIdentityEvidence ?? throw new ArgumentNullException(nameof(lockIdentityEvidence));
            _terminalBuffer = terminalBuffer ?? throw new ArgumentNullException(nameof(terminalBuffer));
            if (!CorrelationsHold()) throw new ArgumentException("Freeze evidence is not fully correlated.", nameof(evidence));
        }

        internal CaptureFrameFreezeTerminalCoordinator IssuedBy => _issuedBy;
        internal CaptureFrameDraftRegistry Drafts => _evidence.Drafts;
        internal CaptureArtifactRegistry Artifacts => _evidence.Artifacts;
        internal CaptureRunInitializationSession RunSession => _runSession;
        internal FreezeTerminalTraceBuffer TerminalBuffer => _terminalBuffer;
        internal CaptureRunRootLayout RootLayout => _lockIdentityEvidence.RootLayout;
        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;
        internal long TestRunId => _lockIdentityEvidence.TestRunId;
        internal string RunInitializationId => _runSession.RunInitializationId;
        internal bool IsValid => CorrelationsHold();

        /// <summary>
        /// O(1) exception-safe structural guard for proof matching: safely
        /// reads the current drafts, artifacts, session, and lock identity
        /// evidence without throwing when the freeze receipt's evidence or
        /// session references have been nulled after issuance. Returns
        /// <c>false</c> for any corrupted reference.
        /// </summary>
        internal bool TryGetIssuedBindings(
            out CaptureFrameDraftRegistry drafts,
            out CaptureArtifactRegistry artifacts,
            out CaptureRunInitializationSession session,
            out CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            drafts = null;
            artifacts = null;
            session = null;
            lockIdentityEvidence = null;

            CaptureEvidenceDraftCoordinator evidence = _evidence;
            CaptureRunLockIdentityEvidence identity = _lockIdentityEvidence;
            if (evidence == null || identity == null)
            {
                return false;
            }

            CaptureFrameDraftRegistry d = evidence.Drafts;
            CaptureArtifactRegistry a = evidence.Artifacts;
            if (d == null || a == null)
            {
                return false;
            }

            if (!identity.IsValid)
            {
                return false;
            }

            drafts = d;
            artifacts = a;
            session = _runSession;
            lockIdentityEvidence = identity;
            return true;
        }

        private bool CorrelationsHold()
        {
            if (_issuedBy == null || _evidence == null || _runSession == null || _lockIdentityEvidence == null || _terminalBuffer == null)
            {
                return false;
            }

            if (!_runSession.IsValid)
            {
                return false;
            }

            if (!_lockIdentityEvidence.IsValid)
            {
                return false;
            }

            if (_runSession.TestRunId != _lockIdentityEvidence.TestRunId)
            {
                return false;
            }

            if (!ReferenceEquals(_runSession.RootLayout, _lockIdentityEvidence.RootLayout))
            {
                return false;
            }

            CaptureRunInitializationSession runSession = _runSession;

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

            if (drafts.Run.TestRunId != runSession.TestRunId)
            {
                return false;
            }

            if (_terminalBuffer.TestRunId != runSession.TestRunId)
            {
                return false;
            }

            if (!_issuedBy.IsFrozenFor(runSession.TestRunId))
            {
                return false;
            }

            return true;
        }
    }
}
