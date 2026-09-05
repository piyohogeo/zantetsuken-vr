using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure in-memory PNG encoder for successful RGBA32 readback buffers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CapturePixelFormat.Rgba32</c> describes a tight-packed byte layout;
    /// this Phase 0 PNG output encodes it as
    /// <c>GraphicsFormat.R8G8B8A8_SRGB</c> following the D-064 SDR/sRGB
    /// profile.
    /// </para>
    /// <para>
    /// The returned <see cref="NativeArray{T}"/> is owned by the caller and must
    /// be disposed. The input buffer is never modified or disposed, and the
    /// input and output do not share native memory. No file I/O is performed.
    /// </para>
    /// <para>
    /// Phase 0.1 invokes this encoder from the dedicated encode worker thread.
    /// The encoded path is the fixed Unity
    /// <c>ImageConversion.EncodeNativeArrayToPNG</c> call; this does not extend
    /// a general thread-safety guarantee to other Unity APIs.
    /// </para>
    /// </remarks>
    public static class CaptureFramePngEncoder
    {
        public static NativeArray<byte> Encode(
            NativeArray<byte> rgbaBytes,
            in CaptureFramePixelLayout layout)
        {
            if (!layout.IsValid)
            {
                throw new ArgumentException("Layout must be valid.", nameof(layout));
            }

            if (layout.Format != CapturePixelFormat.Rgba32)
            {
                throw new ArgumentException("Only Rgba32 layouts are supported.", nameof(layout));
            }

            if (!rgbaBytes.IsCreated)
            {
                throw new ArgumentException("Input buffer is not created.", nameof(rgbaBytes));
            }

            if (rgbaBytes.Length != layout.ByteCount)
            {
                throw new ArgumentException("Input buffer length does not match the layout.", nameof(rgbaBytes));
            }

            NativeArray<byte> png;
            using (ZantetsuProfilerMarkers.CaptureEncode.Auto())
            {
                png = ImageConversion.EncodeNativeArrayToPNG(
                    rgbaBytes,
                    GraphicsFormat.R8G8B8A8_SRGB,
                    (uint)layout.Width,
                    (uint)layout.Height,
                    (uint)layout.RowStrideBytes);
            }

            if (!png.IsCreated || png.Length == 0)
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                throw new InvalidOperationException("PNG encode produced an empty result.");
            }

            return png;
        }
    }
}
