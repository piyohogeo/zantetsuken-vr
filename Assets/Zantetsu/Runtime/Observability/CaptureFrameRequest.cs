using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A single capture frame request: correlation context plus source, eye,
    /// image rectangle and output array index. A value type with no
    /// reference-type fields and no Unity object references.
    /// </summary>
    public readonly struct CaptureFrameRequest
    {
        public CaptureFrameTraceContext TraceContext { get; }

        public CaptureSource Source { get; }

        public CaptureEye Eye { get; }

        public CaptureImageRect ImageRect { get; }

        public int ArrayIndex { get; }

        public bool IsValid => Source != CaptureSource.None && Eye != CaptureEye.None && ImageRect.IsValid && ArrayIndex >= 0;

        public CaptureFrameRequest(
            CaptureFrameTraceContext traceContext,
            CaptureSource source,
            CaptureEye eye,
            CaptureImageRect imageRect,
            int arrayIndex)
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

            TraceContext = traceContext;
            Source = source;
            Eye = eye;
            ImageRect = imageRect;
            ArrayIndex = arrayIndex;
        }
    }
}
