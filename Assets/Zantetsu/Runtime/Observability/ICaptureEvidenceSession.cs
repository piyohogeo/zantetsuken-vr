using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Codec-independent boundary before GPU readback. Implementations own all
    /// readback, media processing, artifact production, queues, drain and join.
    /// They never mutate Draft, Registry, or Trace state.
    /// An accepted submission transfers the surface and emits exactly one frame
    /// completion before any artifact completion for that token. A rejected
    /// submission preserves caller ownership. Published completions are never
    /// duplicated, and stale slot generations are never reused as current work.
    /// </summary>
    internal interface ICaptureEvidenceSession : IDisposable
    {
        CaptureSubmitStatus TrySubmit(
            CaptureFrameEnvelope frame,
            CaptureSurfaceLease surface,
            out CaptureFrameWorkToken token);

        bool TryCollectFrameCompletion(out CaptureFrameCompletion completion);
        bool TryCollectArtifactCompletion(out CaptureArtifactCompletion completion);
        void BeginDrain();
        int CancelQueued();
        bool TryJoin();
    }
}
