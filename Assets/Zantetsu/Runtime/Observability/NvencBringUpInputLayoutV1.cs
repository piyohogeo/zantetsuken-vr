using System;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable input-layout snapshot confirmed before a capture run starts.
    /// It reuses the existing eye, pixel format, and color space enums and the
    /// existing <see cref="CaptureImageRect"/>, and adds only the minimal
    /// orientation value type the existing model lacks.
    /// </summary>
    internal readonly struct NvencBringUpInputLayoutV1
    {
        private readonly bool _initialized;

        internal int ProfileId { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal CaptureImageRect ImageRect { get; }

        internal CaptureEye Eye { get; }

        internal CapturePixelFormat PixelFormat { get; }

        internal GraphicsFormat GraphicsFormat { get; }

        internal CaptureColorSpace ColorSpace { get; }

        internal int SampleCount { get; }

        internal bool HasMipmaps { get; }

        internal bool HasDynamicResolution { get; }

        internal bool IsTextureArray { get; }

        internal int ArrayIndex { get; }

        internal int MipLevel { get; }

        internal NvencInputOrientation Orientation { get; }

        internal bool IsInitialized => _initialized;

        internal NvencBringUpInputLayoutV1(
            int profileId,
            int width,
            int height,
            in CaptureImageRect imageRect,
            CaptureEye eye,
            CapturePixelFormat pixelFormat,
            GraphicsFormat graphicsFormat,
            CaptureColorSpace colorSpace,
            int sampleCount,
            bool hasMipmaps,
            bool hasDynamicResolution,
            bool isTextureArray,
            int arrayIndex,
            int mipLevel,
            NvencInputOrientation orientation)
        {
            if (profileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Profile ID must be greater than zero.");
            }

            if (width < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be at least 1.");
            }

            if (height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be at least 1.");
            }

            if (!imageRect.IsValid)
            {
                throw new ArgumentException("Image rectangle must be valid.", nameof(imageRect));
            }

            if (imageRect.Width != width || imageRect.Height != height)
            {
                throw new ArgumentException("Image rectangle dimensions must match width and height.", nameof(imageRect));
            }

            if (eye == CaptureEye.None)
            {
                throw new ArgumentOutOfRangeException(nameof(eye), eye, "Eye must be a defined non-None value.");
            }

            if (pixelFormat == CapturePixelFormat.None)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Pixel format must be a defined non-None value.");
            }

            if (colorSpace == CaptureColorSpace.None)
            {
                throw new ArgumentOutOfRangeException(nameof(colorSpace), colorSpace, "Color space must be a defined non-None value.");
            }

            if (orientation == NvencInputOrientation.None)
            {
                throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Orientation must be a defined non-None value.");
            }

            if (sampleCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count must be at least 1.");
            }

            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, "Array index must not be negative.");
            }

            if (mipLevel < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel, "Mip level must not be negative.");
            }

            // Checked width * height: a product that overflows Int32 is
            // rejected before any downstream allocation.
            long pixelCount = (long)width * height;
            if (pixelCount > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width * height overflows Int32.");
            }

            ProfileId = profileId;
            Width = width;
            Height = height;
            ImageRect = imageRect;
            Eye = eye;
            PixelFormat = pixelFormat;
            GraphicsFormat = graphicsFormat;
            ColorSpace = colorSpace;
            SampleCount = sampleCount;
            HasMipmaps = hasMipmaps;
            HasDynamicResolution = hasDynamicResolution;
            IsTextureArray = isTextureArray;
            ArrayIndex = arrayIndex;
            MipLevel = mipLevel;
            Orientation = orientation;
            _initialized = true;
        }
    }
}
