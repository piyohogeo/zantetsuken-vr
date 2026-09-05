using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Production PNG encoder for the dedicated encode worker. It delegates to
    /// the fixed Unity <c>ImageConversion.EncodeNativeArrayToPNG</c> path via
    /// <see cref="CaptureFramePngEncoder.Encode"/>. No global hook, static
    /// mutable override, or service locator is used.
    /// </summary>
    internal sealed class PngJsonCaptureFrameEncoder : IPngJsonCaptureFrameEncoder
    {
        internal PngJsonCaptureFrameEncoder()
        {
        }

        internal static PngJsonCaptureFrameEncoder Create()
        {
            return new PngJsonCaptureFrameEncoder();
        }

        public NativeArray<byte> Encode(NativeArray<byte> rgbaBytes, in CaptureFramePixelLayout layout)
        {
            return CaptureFramePngEncoder.Encode(rgbaBytes, layout);
        }
    }
}
