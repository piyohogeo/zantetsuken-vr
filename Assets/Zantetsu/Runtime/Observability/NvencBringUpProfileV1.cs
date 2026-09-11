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
    /// <para>
    /// It also fixes the encoder request itself: the one H.264 High,
    /// all-IDR, constant-QP 28 configuration this bring-up asks for. Those
    /// values are the request, not an observation and not a capability - what
    /// a session accepts is answered when it is initialized.
    /// </para>
    /// <para>
    /// The category values are project-owned canonical identifiers, meant for
    /// the Run Manifest and for one mapping site later on. They are not NVIDIA
    /// GUID names, enum spellings, or ABI values, and nothing here copies an
    /// NVENC constant: the SDK's GUIDs and structures stay behind the native
    /// boundary that will translate these identifiers exactly once.
    /// </para>
    /// <para>
    /// This type is immutable, owns and disposes nothing, does not implement
    /// <see cref="IDisposable"/>, is not a MonoBehaviour, ScriptableObject, or
    /// singleton, and performs no Unity static API access, file I/O, logging,
    /// or queue operation. It never probes the real OS, GPU, or NVENC.
    /// </para>
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

        // ---- The fixed encoder request ----
        //
        // Project-owned canonical identifiers for the Run Manifest, not NVIDIA
        // GUID names or ABI values.
        public const string CodecId = "h264";
        public const string EncodeProfileId = "high";
        public const string PresetId = "p1";
        public const string TuningId = "low-latency";
        public const string RateControlId = "constant-qp";
        public const string InputFormatId = "nv12";
        public const string ChromaFormatId = "4:2:0";
        public const string LevelId = "auto";
        public const string OutputFormatId = "annex-b";

        /// <summary>
        /// The requested frame rate as the encoder takes it, as a numerator
        /// over a denominator. It is the same rate as
        /// <see cref="TargetFramesPerSecond"/>, expressed the way the encoder
        /// request needs it rather than as a second, independent value.
        /// </summary>
        public const int FrameRateNumerator = TargetFramesPerSecond;
        public const int FrameRateDenominator = 1;

        /// <summary>
        /// All-IDR: the picture-type decision is the caller's, every frame is
        /// forced to IDR, and the GOP is one picture long, so no frame ever
        /// references another.
        /// </summary>
        public const bool EnablePictureTypeDecision = false;
        public const int GopLength = 1;
        public const int IdrPeriod = 1;
        public const int FrameIntervalP = 1;
        public const bool ForceIdrEveryFrame = true;

        /// <summary>
        /// Constant QP 28. All three values are part of one complete
        /// rate-control request; carrying an inter-picture QP does not ask for
        /// a P or B picture, which the all-IDR request above rules out.
        /// </summary>
        public const int QpIntra = 28;
        public const int QpInterP = 28;
        public const int QpInterB = 28;

        /// <summary>
        /// Parameter-set repetition is requested once, here: the sequence and
        /// picture parameter sets are repeated with each IDR by this setting
        /// alone, and no per-frame parameter-set output is asked for on top of
        /// it.
        /// </summary>
        public const bool RepeatSequenceAndPictureParameterSets = true;
        public const bool OutputAccessUnitDelimiter = false;
        public const bool DisableSequenceAndPictureParameterSets = false;

        public const bool ProgressiveEncoding = true;
        public const bool EnableEncodeAsync = true;
        public const bool EnableOutputInVideoMemory = false;

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

        /// <summary>
        /// The encoded picture size, which is the fixed input size: this
        /// bring-up neither scales nor crops.
        /// </summary>
        internal int EncodeWidth => Width;

        internal int EncodeHeight => Height;

        /// <summary>
        /// The largest picture size this request will ever use. It is the same
        /// fixed size, since the Run never changes resolution; it is not a
        /// device capability, which the capability snapshot reports
        /// separately.
        /// </summary>
        internal int MaximumEncodeWidth => Width;

        internal int MaximumEncodeHeight => Height;
    }
}
