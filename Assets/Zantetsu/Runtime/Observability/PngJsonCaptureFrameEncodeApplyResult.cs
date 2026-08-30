using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread result of applying a successful encode completion. The PNG
    /// is caller-owned and must be disposed or transferred exactly once.
    /// </summary>
    internal readonly struct PngJsonCaptureFrameEncodeApplyResult
    {
        internal CaptureFrameRequest FrameRequest { get; }

        internal NativeArray<byte> PngBytes { get; }

        internal bool IsValid => FrameRequest.IsValid && PngBytes.IsCreated && PngBytes.Length > 0;

        internal PngJsonCaptureFrameEncodeApplyResult(
            in CaptureFrameRequest frameRequest,
            NativeArray<byte> pngBytes)
        {
            if (!frameRequest.IsValid)
            {
                throw new ArgumentException("Frame request must be valid.", nameof(frameRequest));
            }

            if (!pngBytes.IsCreated || pngBytes.Length <= 0)
            {
                throw new ArgumentException("PNG bytes must be created and non-empty.", nameof(pngBytes));
            }

            FrameRequest = frameRequest;
            PngBytes = pngBytes;
        }
    }
}
