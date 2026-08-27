using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, fixed-capacity pair of a phase zero
    /// <see cref="CaptureFrameProfile"/> and its matching
    /// <see cref="CaptureTraceProfile"/>. Both profiles always share the same
    /// profile ID, so frame and trace capacity settings cannot diverge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CreateUnityLeftEye"/> builds the pair from a single profile ID
    /// and image rectangle: the frame profile is the existing
    /// <see cref="CaptureFrameProfile.CreatePhaseZeroUnityLeftEye"/> result used
    /// unchanged, and the trace profile fixes the phase zero capacities to
    /// <c>PostRollCapacity = 4096</c>, <c>MaxInFlightDraftCount = 32</c>, and
    /// <c>MaxDraftCountPerRun = 10000</c>.
    /// </para>
    /// <para>
    /// There is no public constructor, so an inconsistent frame/trace pair
    /// cannot be built externally. The type is immutable, owns and disposes
    /// nothing, does not implement <see cref="IDisposable"/>, is not a
    /// MonoBehaviour, ScriptableObject, or singleton, and performs no Unity
    /// static API access, file I/O, logging, trace recording, or queue
    /// operation. Its state never changes after construction.
    /// </para>
    /// </remarks>
    public sealed class PhaseZeroCaptureProfileSet
    {
        private readonly CaptureFrameProfile _frameProfile;
        private readonly CaptureTraceProfile _traceProfile;

        private PhaseZeroCaptureProfileSet(CaptureFrameProfile frameProfile, CaptureTraceProfile traceProfile)
        {
            _frameProfile = frameProfile;
            _traceProfile = traceProfile;
        }

        public CaptureFrameProfile FrameProfile => _frameProfile;

        public CaptureTraceProfile TraceProfile => _traceProfile;

        public static PhaseZeroCaptureProfileSet CreateUnityLeftEye(
            int profileId,
            in CaptureImageRect imageRect)
        {
            CaptureFrameProfile frameProfile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(profileId, imageRect);
            CaptureTraceProfile traceProfile = new CaptureTraceProfile(profileId, 4096, 32, 10000);

            return new PhaseZeroCaptureProfileSet(frameProfile, traceProfile);
        }
    }
}
