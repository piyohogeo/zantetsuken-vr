using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Format-neutral main-thread facade. It knows no media implementation,
    /// encoded buffer, file extension, or backend queue type.
    /// </summary>
    internal sealed class CaptureEvidenceCoordinator
    {
        private readonly ICaptureEvidenceSession _session;

        internal CaptureEvidenceCoordinator(ICaptureEvidenceSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.MaximumArtifactCountPerSubmission < 0)
                throw new ArgumentException("Session artifact maximum must not be negative.", nameof(session));
        }

        internal int MaximumArtifactCountPerSubmission => _session.MaximumArtifactCountPerSubmission;

        internal CaptureSubmitStatus TrySubmit(
            CaptureFrameEnvelope frame,
            CaptureSurfaceLease surface,
            out CaptureFrameWorkToken token)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (!surface.IsCallerOwned) throw new ArgumentException("Surface must be caller-owned.", nameof(surface));

            CaptureSubmitStatus status = _session.TrySubmit(frame, surface, out token);
            if (status == CaptureSubmitStatus.Accepted)
            {
                if (!token.IsValid
                    || token.TestRunId != frame.TestRunId
                    || token.CaptureFrameId != frame.CaptureFrameId
                    || surface.IsCallerOwned)
                    throw new InvalidOperationException("Accepted submission must transfer the surface and return a correlated token.");
            }
            else if (status == CaptureSubmitStatus.Backpressured || status == CaptureSubmitStatus.NotAccepting)
            {
                if (token.IsValid || !surface.IsCallerOwned) throw new InvalidOperationException("Rejected submission must preserve caller ownership.");
            }
            else
            {
                throw new InvalidOperationException("Backend returned an undefined submit status.");
            }

            return status;
        }

        internal bool TryCollectFrameCompletion(out CaptureFrameCompletion completion) => _session.TryCollectFrameCompletion(out completion);
        internal bool TryCollectArtifactCompletion(out CaptureArtifactCompletion completion) => _session.TryCollectArtifactCompletion(out completion);
        internal void BeginDrain() => _session.BeginDrain();
        internal int CancelQueued() => _session.CancelQueued();
        internal bool TryJoin() => _session.TryJoin();
    }
}
