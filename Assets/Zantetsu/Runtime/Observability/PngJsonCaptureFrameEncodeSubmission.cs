using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A one-shot encode submission. It owns the readback release obligation
    /// until an encode service accepts it.
    /// </summary>
    internal sealed class PngJsonCaptureFrameEncodeSubmission
    {
        private CaptureFrameReadbackPayloadLease _payload;

        internal CaptureFrameRequest FrameRequest { get; }

        internal CaptureFrameWorkStage Stage => CaptureFrameWorkStage.ReadbackCompleted;

        internal bool HasPayload => _payload != null;

        internal PngJsonCaptureFrameEncodeSubmission(CaptureFrameReadbackPayloadLease payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (!payload.IsCallerOwned)
            {
                throw new ArgumentException("Payload must be caller-owned.", nameof(payload));
            }

            FrameRequest = payload.FrameRequest;
            _payload = payload;
        }

        internal CaptureFrameReadbackPayloadLease Accept(
            Guid serviceOwner,
            in CaptureFrameWorkToken workToken)
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
