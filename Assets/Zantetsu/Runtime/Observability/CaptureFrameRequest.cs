using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A single capture frame request: correlation context, source, eye, image
    /// rectangle, output array index and pixel layout. A value type with no
    /// reference-type fields and no Unity object references.
    /// </summary>
    public readonly struct CaptureFrameRequest
    {
        public CaptureFrameTraceContext TraceContext { get; }

        public CaptureSource Source { get; }

        public CaptureEye Eye { get; }

        public CaptureImageRect ImageRect { get; }

        public int ArrayIndex { get; }

        public CaptureFramePixelLayout PixelLayout { get; }

        public int RequiredByteCount { get; }

        public bool IsValid =>
            Source != CaptureSource.None &&
            Eye != CaptureEye.None &&
            ImageRect.IsValid &&
            ArrayIndex >= 0 &&
            PixelLayout.IsValid &&
            PixelLayout.Width == ImageRect.Width &&
            PixelLayout.Height == ImageRect.Height;

        public CaptureFrameRequest(
            CaptureFrameTraceContext traceContext,
            CaptureSource source,
            CaptureEye eye,
            CaptureImageRect imageRect,
            int arrayIndex,
            CapturePixelFormat pixelFormat)
        {
            if (source != CaptureSource.UnityRenderTexture && source != CaptureSource.OpenXRProjection)
            {
                throw new ArgumentException("Source must be a defined non-None value.", nameof(source));
            }

            if (eye != CaptureEye.Left && eye != CaptureEye.Right)
            {
                throw new ArgumentException("Eye must be a defined non-None value.", nameof(eye));
            }

            if (!imageRect.IsValid)
            {
                throw new ArgumentException("Image rect must be valid.", nameof(imageRect));
            }

            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, "Array index must not be negative.");
            }

            CaptureFramePixelLayout layout = new CaptureFramePixelLayout(pixelFormat, imageRect.Width, imageRect.Height);

            TraceContext = traceContext;
            Source = source;
            Eye = eye;
            ImageRect = imageRect;
            ArrayIndex = arrayIndex;
            PixelLayout = layout;
            RequiredByteCount = layout.ByteCount;
        }
    }
}
