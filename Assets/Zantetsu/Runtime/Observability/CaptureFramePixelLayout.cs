using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Tight-packed (no row padding) pixel layout for a capture frame buffer.
    /// A value type with no reference-type fields. Computations use long
    /// arithmetic so overflow is detected before any array is allocated.
    /// </summary>
    public readonly struct CaptureFramePixelLayout
    {
        public CapturePixelFormat Format { get; }

        public int Width { get; }

        public int Height { get; }

        public int BytesPerPixel { get; }

        public int RowStrideBytes { get; }

        public int ByteCount { get; }

        public bool IsValid { get; }

        public CaptureFramePixelLayout(
            CapturePixelFormat format,
            int width,
            int height)
        {
            if (format != CapturePixelFormat.Rgba32)
            {
                throw new ArgumentException("Format must be a defined non-None value.", nameof(format));
            }

            if (width < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be at least 1.");
            }

            if (height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be at least 1.");
            }

            const int bytesPerPixel = 4;

            long rowStride = (long)width * bytesPerPixel;
            if (rowStride > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Row stride exceeds Int32.MaxValue.");
            }

            long byteCount = rowStride * height;
            if (byteCount > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Total byte count exceeds Int32.MaxValue.");
            }

            Format = format;
            Width = width;
            Height = height;
            BytesPerPixel = bytesPerPixel;
            RowStrideBytes = (int)rowStride;
            ByteCount = (int)byteCount;
            IsValid = true;
        }
    }
}
