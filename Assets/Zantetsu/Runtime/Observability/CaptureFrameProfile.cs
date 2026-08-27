using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable logical capture configuration for one capture profile. It
    /// binds the capture profile ID, target FPS, source, eye, image rectangle,
    /// array index, and pixel format, and produces a cadence selector and a
    /// record factory that share this same configuration, so cadence and record
    /// settings cannot diverge under the same profile ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile ID is a semantic identifier of the configuration content. This
    /// type does not manage global uniqueness of the ID; the caller is
    /// responsible for assigning distinct IDs to distinct configurations.
    /// </para>
    /// <para>
    /// Target FPS validation is delegated to the
    /// <see cref="CaptureFrameCadenceSelector"/> constructor, and
    /// source/eye/rectangle/array-index/pixel-format validation is delegated to
    /// a <see cref="CaptureFrameRequest"/> built with a default trace context.
    /// <see cref="MinimumIntervalSeconds"/> and <see cref="PixelLayout"/> are
    /// the values held by those validated instances, never recomputed here.
    /// </para>
    /// <para>
    /// <see cref="CreateCadenceSelector"/> and <see cref="CreateRecordFactory"/>
    /// each return an independent instance. This profile owns none of the
    /// generated selectors, factories, runs, or sequences, and does not
    /// implement <see cref="IDisposable"/>. It is not a MonoBehaviour,
    /// ScriptableObject, or singleton, references no Unity static API, time,
    /// frame counter, XR API, or Graphics API, and performs no file I/O,
    /// logging, trace recording, or queue registration. Its state never changes
    /// after construction.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameProfile
    {
        private readonly int _profileId;
        private readonly double _targetFramesPerSecond;
        private readonly double _minimumIntervalSeconds;
        private readonly CaptureSource _source;
        private readonly CaptureEye _eye;
        private readonly CaptureImageRect _imageRect;
        private readonly int _arrayIndex;
        private readonly CapturePixelFormat _pixelFormat;
        private readonly CaptureFramePixelLayout _pixelLayout;

        public CaptureFrameProfile(
            int profileId,
            double targetFramesPerSecond,
            CaptureSource source,
            CaptureEye eye,
            in CaptureImageRect imageRect,
            int arrayIndex,
            CapturePixelFormat pixelFormat)
        {
            if (profileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Profile ID must be greater than zero.");
            }

            // Delegate target FPS validation to the cadence selector constructor.
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(targetFramesPerSecond);

            // Delegate source/eye/rect/array-index/pixel-format validation to a
            // capture request built with a default trace context.
            CaptureFrameRequest request = new CaptureFrameRequest(
                default,
                source,
                eye,
                imageRect,
                arrayIndex,
                pixelFormat);

            _profileId = profileId;
            _targetFramesPerSecond = targetFramesPerSecond;
            _minimumIntervalSeconds = selector.MinimumIntervalSeconds;
            _source = source;
            _eye = eye;
            _imageRect = imageRect;
            _arrayIndex = arrayIndex;
            _pixelFormat = pixelFormat;
            _pixelLayout = request.PixelLayout;
        }

        public int ProfileId => _profileId;

        public double TargetFramesPerSecond => _targetFramesPerSecond;

        public double MinimumIntervalSeconds => _minimumIntervalSeconds;

        public CaptureSource Source => _source;

        public CaptureEye Eye => _eye;

        public CaptureImageRect ImageRect => _imageRect;

        public int ArrayIndex => _arrayIndex;

        public CapturePixelFormat PixelFormat => _pixelFormat;

        public CaptureFramePixelLayout PixelLayout => _pixelLayout;

        public CaptureFrameCadenceSelector CreateCadenceSelector()
        {
            return new CaptureFrameCadenceSelector(_targetFramesPerSecond);
        }

        public CaptureFrameRecordFactory CreateRecordFactory(
            CaptureRunReference run,
            CaptureFrameIdSequence captureFrameIds)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (captureFrameIds == null)
            {
                throw new ArgumentNullException(nameof(captureFrameIds));
            }

            if (run.CaptureProfileId != _profileId)
            {
                throw new ArgumentException("The run reference's capture profile ID must match the profile ID.", nameof(run));
            }

            return new CaptureFrameRecordFactory(
                run,
                captureFrameIds,
                _source,
                _eye,
                _imageRect,
                _arrayIndex,
                _pixelFormat);
        }

        public static CaptureFrameProfile CreatePhaseZeroUnityLeftEye(
            int profileId,
            in CaptureImageRect imageRect)
        {
            return new CaptureFrameProfile(
                profileId,
                CaptureFrameCadenceSelector.PhaseZeroTargetFramesPerSecond,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                imageRect,
                0,
                CapturePixelFormat.Rgba32);
        }
    }
}
