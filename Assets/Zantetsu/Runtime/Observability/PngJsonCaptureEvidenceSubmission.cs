using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// One-shot evidence submission bundling the codec-independent envelope,
    /// the readback payload ownership, and the backend-issued work token that
    /// stamps every produced completion. The readback release obligation stays
    /// with the caller until an evidence worker accepts the submission.
    /// </summary>
    internal sealed class PngJsonCaptureEvidenceSubmission
    {
        private CaptureFrameReadbackPayloadLease _payload;

        internal CaptureFrameEnvelope Frame { get; }

        internal CaptureFrameWorkToken CompletionToken { get; }

        internal CaptureFrameRequest FrameRequest => _payload.FrameRequest;

        internal bool HasPayload => _payload != null;

        internal PngJsonCaptureEvidenceSubmission(
            CaptureFrameEnvelope frame,
            CaptureFrameReadbackPayloadLease payload,
            in CaptureFrameWorkToken completionToken)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (!payload.IsCallerOwned)
            {
                throw new ArgumentException("Payload must be caller-owned.", nameof(payload));
            }

            if (!completionToken.IsValid || completionToken.CaptureFrameId != frame.CaptureFrameId)
            {
                throw new ArgumentException("Completion token must be valid and correlated to the frame.", nameof(completionToken));
            }

            Frame = frame;
            _payload = payload;
            CompletionToken = completionToken;
        }

        internal CaptureFrameReadbackPayloadLease Accept(Guid serviceOwner, in CaptureFrameWorkToken workToken)
        {
            if (_payload == null)
            {
                throw new InvalidOperationException("The submission was already accepted.");
            }

            CaptureFrameReadbackPayloadLease accepted = _payload;
            accepted.TransferToService(serviceOwner, workToken);
            _payload = null;
            return accepted;
        }
    }
}
