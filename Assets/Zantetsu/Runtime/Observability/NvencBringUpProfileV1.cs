using System;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Phase 0.11 NVENC bring-up profile. It fixes the short
    /// Windows / NVIDIA / D3D11 WDDM async-encode bring-up contract: SDR/sRGB,
    /// left eye, 30 fps, 1280x720 RGBA8 sRGB input, top-left orientation, and
    /// BT.709 limited-range NV12 conversion, together with every fixed
    /// capacity, cadence, and deadline value from DESIGN.md. The caller
    /// supplies only a positive profile ID; every other value is fixed.
    /// </summary>
    /// <remarks>
    /// This type is immutable, owns and disposes nothing, does not implement
    /// <see cref="IDisposable"/>, is not a MonoBehaviour, ScriptableObject, or
    /// singleton, and performs no Unity static API access, file I/O, logging,
    /// or queue operation. It never probes the real OS, GPU, or NVENC.
    /// </remarks>
    internal sealed class NvencBringUpProfileV1
    {
        public const int Width = 1280;
        public const int Height = 720;
        public const int TargetFramesPerSecond = 30;
        public const int CadenceTickCount = 120;
        public const int SubmissionWindowDeadlineMs = 4000;
        public const int FinalizationDeadlineMs = 30000;
        public const int RecoveryFinalizationDeadlineMs = 10000;

        public const int WorkSlotCount = 8;
        public const int EncodeSampleSlotCount = 8;
        public const int SourceSurfaceLeaseCapacity = 8;
        public const int GpuConversionSyncCapacity = 8;
        public const int SubmissionQueueCapacity = 8;
        public const int SubmitToOutputQueueCapacity = 8;
        public const int FrameCompletionQueueCapacity = 8;

        public const long MaxAccessUnitByteLength = 16L * 1024L * 1024L;

        public const long MaxChunkByteLength = 256L * 1024L * 1024L;

        private static readonly CaptureImageRect ImageRectValue = new CaptureImageRect(0, 0, Width, Height);

        private readonly int _profileId;

        internal NvencBringUpProfileV1(int profileId)
        {
            if (profileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Profile ID must be greater than zero.");
            }

            _profileId = profileId;
        }

        internal int ProfileId => _profileId;

        internal CaptureSource Source => CaptureSource.UnityRenderTexture;

        internal CaptureEye Eye => CaptureEye.Left;

        internal CaptureImageRect ImageRect => ImageRectValue;

        internal CapturePixelFormat PixelFormat => CapturePixelFormat.Rgba32;

        internal GraphicsFormat GraphicsFormat => GraphicsFormat.R8G8B8A8_SRGB;

        internal CaptureColorSpace ColorSpace => CaptureColorSpace.Srgb;

        internal NvencInputOrientation Orientation => NvencInputOrientation.TopLeft;

        internal NvencColorConversion ColorConversion => NvencColorConversion.Bt709LimitedRangeNv12;

        internal int SampleCount => 1;

        internal bool HasMipmaps => false;

        internal bool HasDynamicResolution => false;

        internal bool IsTextureArray => false;

        internal int ArrayIndex => 0;

        internal int MipLevel => 0;
    }
}
