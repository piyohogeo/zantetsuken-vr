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

        /// <summary>
        /// Allocation-free, field-by-field equality used by the pump to verify
        /// that a dequeued request is identical to the peeked one. Avoids
        /// boxing, reflection, string generation, and <c>ValueType.Equals</c>.
        /// </summary>
        internal bool IdenticalTo(in CaptureFrameRequest other)
        {
            return
                TraceContext.Timestamp == other.TraceContext.Timestamp &&
                TraceContext.UnityFrameId == other.TraceContext.UnityFrameId &&
                TraceContext.FixedStepId == other.TraceContext.FixedStepId &&
                TraceContext.ThreadId == other.TraceContext.ThreadId &&
                TraceContext.CaptureFrameId == other.TraceContext.CaptureFrameId &&
                TraceContext.OpenXRFrameId == other.TraceContext.OpenXRFrameId &&
                TraceContext.TestRunId == other.TraceContext.TestRunId &&
                TraceContext.SlashId == other.TraceContext.SlashId &&
                TraceContext.FrontEdgeId == other.TraceContext.FrontEdgeId &&
                TraceContext.ObjectId == other.TraceContext.ObjectId &&
                TraceContext.ObjectGeneration == other.TraceContext.ObjectGeneration &&
                TraceContext.TaskId == other.TraceContext.TaskId &&
                Source == other.Source &&
                Eye == other.Eye &&
                ImageRect.X == other.ImageRect.X &&
                ImageRect.Y == other.ImageRect.Y &&
                ImageRect.Width == other.ImageRect.Width &&
                ImageRect.Height == other.ImageRect.Height &&
                ArrayIndex == other.ArrayIndex &&
                PixelLayout.Format == other.PixelLayout.Format &&
                PixelLayout.Width == other.PixelLayout.Width &&
                PixelLayout.Height == other.PixelLayout.Height &&
                PixelLayout.BytesPerPixel == other.PixelLayout.BytesPerPixel &&
                PixelLayout.RowStrideBytes == other.PixelLayout.RowStrideBytes &&
                PixelLayout.ByteCount == other.PixelLayout.ByteCount;
        }
    }
}
