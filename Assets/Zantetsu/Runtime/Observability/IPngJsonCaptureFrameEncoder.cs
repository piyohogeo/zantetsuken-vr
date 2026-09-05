using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Internal PNG encoder boundary callable from the dedicated encode worker
    /// thread. Implementations never touch Draft, Registry, Trace, filesystem,
    /// dispatcher, or pool state.
    /// </summary>
    internal interface IPngJsonCaptureFrameEncoder
    {
        NativeArray<byte> Encode(NativeArray<byte> rgbaBytes, in CaptureFramePixelLayout layout);
    }
}
