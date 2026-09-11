using System;
using System.Runtime.InteropServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The managed owner of one retained native NVENC encoder session: opened
    /// once, held for as long as the caller keeps it, and closed once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is the native side's; what is kept here is the opaque handle
    /// that names it. No device, encoder, or function table ever crosses the
    /// boundary, and this type exposes none of them.
    /// </para>
    /// <para>
    /// A configuration that genuinely cannot encode - a driver older than the
    /// build requires, no current D3D11 device, or the driver reporting that
    /// this device has no encoder - is not an error: <see cref="TryOpen"/>
    /// returns false, as it does in the Editor and off Windows, where the
    /// native library is never named. Everything else is: an observation the
    /// native side could not complete, a refused write, or an ABI version this
    /// build does not speak all throw.
    /// </para>
    /// <para>
    /// Disposing closes the session exactly once. A close the driver refuses
    /// throws and keeps the handle, because the session is still open and still
    /// this owner's - and the attempt is spent, so a later dispose throws
    /// without asking the native side to destroy that encoder again. A dispose
    /// after a successful close does nothing. There is no finalizer, safe
    /// handle, registry, or generation counter behind any of it.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeEncoderSessionOwner : IDisposable
    {
        /// <summary>The session ABI version this build speaks.</summary>
        internal const uint AbiVersion = 1;

        private const uint StatusOk = 1;
        private const uint StatusUnsupported = 2;
        private const uint StatusFailed = 3;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const string NativeLibraryName = "ZantetsuNvenc";

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOpenResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal ulong SessionOwner;
            internal uint LastWin32Error;
            internal int LastNvencStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCapabilityResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal uint SupportsAsyncEncode;
            internal uint SupportsH264Encode;
            internal uint SupportsH264HighProfile;
            internal uint SupportsNv12Input;
            internal int MaximumEncodeWidth;
            internal int MaximumEncodeHeight;
            internal int LastNvencStatus;
        }

        // The project's own category identifiers. What they mean in NVENC's
        // terms is the native side's business; no GUID or SDK enumeration is
        // defined here.
        private const uint CodecH264 = 1;
        private const uint ProfileHigh = 1;
        private const uint PresetP1 = 1;
        private const uint TuningLowLatency = 1;
        private const uint RateControlConstantQp = 1;
        private const uint ChromaFormat420 = 1;
        private const uint LevelAuto = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInitializeRequestV1
        {
            internal uint AbiVersion;

            internal uint CodecId;
            internal uint ProfileId;
            internal uint PresetId;
            internal uint TuningId;
            internal uint RateControlId;
            internal uint ChromaFormatId;
            internal uint LevelId;

            internal uint EncodeWidth;
            internal uint EncodeHeight;
            internal uint MaximumEncodeWidth;
            internal uint MaximumEncodeHeight;
            internal uint FrameRateNumerator;
            internal uint FrameRateDenominator;

            internal uint EnablePictureTypeDecision;
            internal uint GopLength;
            internal uint IdrPeriod;
            internal int FrameIntervalP;

            internal uint QpIntra;
            internal uint QpInterP;
            internal uint QpInterB;

            internal uint RepeatSequenceAndPictureParameterSets;
            internal uint OutputAccessUnitDelimiter;
            internal uint DisableSequenceAndPictureParameterSets;

            internal uint ProgressiveEncoding;
            internal uint EnableEncodeAsync;
            internal uint EnableOutputInVideoMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInitializeResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastNvencStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCompletionEventResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal uint LastWin32Error;
            internal int LastNvencStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInputSurfaceResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastHResult;
            internal int LastNvencStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOutputBufferResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastNvencStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCloseResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal uint LastWin32Error;
            internal int LastNvencStatus;
        }

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencOpenSessionV1(
            ref NativeOpenResultV1 destination, uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencObserveSessionCapabilityV1(
            ulong sessionOwner, ref NativeCapabilityResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencInitializeSessionEncoderV1(
            ulong sessionOwner, ref NativeInitializeRequestV1 request, uint requestSize,
            ref NativeInitializeResultV1 destination, uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencPrepareSessionCompletionEventsV1(
            ulong sessionOwner, ref NativeCompletionEventResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencReleaseSessionCompletionEventsV1(
            ulong sessionOwner, ref NativeCompletionEventResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencPrepareSessionInputSurfacesV1(
            ulong sessionOwner, ref NativeInputSurfaceResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencReleaseSessionInputSurfacesV1(
            ulong sessionOwner, ref NativeInputSurfaceResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencPrepareSessionOutputBuffersV1(
            ulong sessionOwner, ref NativeOutputBufferResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencReleaseSessionOutputBuffersV1(
            ulong sessionOwner, ref NativeOutputBufferResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencCloseSessionV1(
            ulong sessionOwner, ref NativeCloseResultV1 destination, uint destinationSize);
#endif

        private ulong _sessionOwner;
        private bool _closeAttempted;
        private bool _completionEventsPrepareAttempted;
        private bool _completionEventsReleaseAttempted;
        private bool _completionEventsPrepared;
        private bool _outputBuffersPrepareAttempted;
        private bool _outputBuffersReleaseAttempted;
        private bool _outputBuffersPrepared;
        private bool _inputSurfacesPrepareAttempted;
        private bool _inputSurfacesReleaseAttempted;
        private bool _inputSurfacesPrepared;

        private NvencNativeEncoderSessionOwner(ulong sessionOwner)
        {
            _sessionOwner = sessionOwner;
        }

        /// <summary>True while this owner still holds an open session.</summary>
        internal bool IsOpen => _sessionOwner != 0;

        /// <summary>
        /// Opens one session. Returns false with no owner where a session
        /// cannot be opened at all; throws where the native side could not
        /// complete the attempt or broke the ABI.
        /// </summary>
        internal static bool TryOpen(out NvencNativeEncoderSessionOwner owner)
        {
            owner = null;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeOpenResultV1 result = default;
            int written = ZantetsuNvencOpenSessionV1(
                ref result, (uint)Marshal.SizeOf(typeof(NativeOpenResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native encoder session open was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            switch (result.Status)
            {
                case StatusOk:
                    if (result.SessionOwner == 0)
                    {
                        throw new InvalidOperationException(
                            "The native encoder session reported success without a session.");
                    }

                    owner = new NvencNativeEncoderSessionOwner(result.SessionOwner);
                    return true;

                case StatusUnsupported:
                    return false;

                case StatusFailed:
                    throw new InvalidOperationException(
                        "The native encoder session could not be opened (win32 error "
                        + result.LastWin32Error + ", NVENCSTATUS " + result.LastNvencStatus
                        + ").");

                default:
                    throw new InvalidOperationException(
                        "The native encoder session returned an undefined status: "
                        + result.Status + ".");
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Observes this session's encoder capabilities, once. The session
        /// stays open whatever the answer is, and a second call - or one after
        /// the close was attempted - is refused by the native side and throws
        /// here.
        /// </summary>
        /// <remarks>
        /// An observation the native side could not complete, an ABI version
        /// this build does not speak, and a boolean that is neither zero nor
        /// one are all broken contracts and throw. Nothing is retried, cached,
        /// polled, or answered from a second session.
        /// </remarks>
        internal NvencEncoderCapabilityObservationV1 ObserveCapabilities()
        {
            if (_sessionOwner == 0)
            {
                throw new InvalidOperationException(
                    "This encoder session is not open; there is nothing to observe.");
            }

            if (_closeAttempted)
            {
                throw new InvalidOperationException(
                    "This encoder session's close was already attempted; it is not observed after that.");
            }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeCapabilityResultV1 result = default;
            int written = ZantetsuNvencObserveSessionCapabilityV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeCapabilityResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native encoder capability observation was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The native encoder capabilities could not be observed (status "
                    + result.Status + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }

            return new NvencEncoderCapabilityObservationV1(
                ToBoolean(result.SupportsAsyncEncode, "supportsAsyncEncode"),
                ToBoolean(result.SupportsH264Encode, "supportsH264Encode"),
                ToBoolean(result.SupportsH264HighProfile, "supportsH264HighProfile"),
                ToBoolean(result.SupportsNv12Input, "supportsNv12Input"),
                result.MaximumEncodeWidth,
                result.MaximumEncodeHeight);
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif
        }

        /// <summary>
        /// Initializes this session's encoder with the profile's fixed
        /// request, once.
        /// </summary>
        /// <remarks>
        /// The request carries exactly what the initialization sets, built
        /// from the profile's own constants. Whether the capability snapshot
        /// and the input layout admit this profile is the caller's to settle
        /// beforehand; nothing is re-evaluated here. A refused initialization
        /// throws and leaves the session open for the caller to close, and
        /// nothing is retried, cached, or answered from a new session.
        /// </remarks>
        internal void InitializeEncoder(NvencBringUpProfileV1 profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (_sessionOwner == 0)
            {
                throw new InvalidOperationException(
                    "This encoder session is not open; there is nothing to initialize.");
            }

            if (_closeAttempted)
            {
                throw new InvalidOperationException(
                    "This encoder session's close was already attempted; it is not initialized after that.");
            }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeInitializeRequestV1 request = new NativeInitializeRequestV1
            {
                AbiVersion = AbiVersion,

                CodecId = CodecH264,
                ProfileId = ProfileHigh,
                PresetId = PresetP1,
                TuningId = TuningLowLatency,
                RateControlId = RateControlConstantQp,
                ChromaFormatId = ChromaFormat420,
                LevelId = LevelAuto,

                EncodeWidth = (uint)profile.EncodeWidth,
                EncodeHeight = (uint)profile.EncodeHeight,
                MaximumEncodeWidth = (uint)profile.MaximumEncodeWidth,
                MaximumEncodeHeight = (uint)profile.MaximumEncodeHeight,
                FrameRateNumerator = NvencBringUpProfileV1.FrameRateNumerator,
                FrameRateDenominator = NvencBringUpProfileV1.FrameRateDenominator,

                EnablePictureTypeDecision =
                    NvencBringUpProfileV1.EnablePictureTypeDecision ? 1u : 0u,
                GopLength = NvencBringUpProfileV1.GopLength,
                IdrPeriod = NvencBringUpProfileV1.IdrPeriod,
                FrameIntervalP = NvencBringUpProfileV1.FrameIntervalP,

                QpIntra = NvencBringUpProfileV1.QpIntra,
                QpInterP = NvencBringUpProfileV1.QpInterP,
                QpInterB = NvencBringUpProfileV1.QpInterB,

                RepeatSequenceAndPictureParameterSets =
                    NvencBringUpProfileV1.RepeatSequenceAndPictureParameterSets ? 1u : 0u,
                OutputAccessUnitDelimiter =
                    NvencBringUpProfileV1.OutputAccessUnitDelimiter ? 1u : 0u,
                DisableSequenceAndPictureParameterSets =
                    NvencBringUpProfileV1.DisableSequenceAndPictureParameterSets ? 1u : 0u,

                ProgressiveEncoding = NvencBringUpProfileV1.ProgressiveEncoding ? 1u : 0u,
                EnableEncodeAsync = NvencBringUpProfileV1.EnableEncodeAsync ? 1u : 0u,
                EnableOutputInVideoMemory =
                    NvencBringUpProfileV1.EnableOutputInVideoMemory ? 1u : 0u,
            };

            NativeInitializeResultV1 result = default;
            int written = ZantetsuNvencInitializeSessionEncoderV1(
                _sessionOwner,
                ref request,
                (uint)Marshal.SizeOf(typeof(NativeInitializeRequestV1)),
                ref result,
                (uint)Marshal.SizeOf(typeof(NativeInitializeResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native encoder initialization was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The native encoder could not be initialized (status "
                    + result.Status + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif
        }

        /// <summary>
        /// Creates and registers this session's fixed set of completion
        /// events - all of them, or none.
        /// </summary>
        /// <remarks>
        /// The events are the native side's: no handle, slot state, or count
        /// is exposed here, and nothing waits on them yet. One owner prepares
        /// once, whatever the first attempt concluded, and a failure unwinds
        /// what it took.
        /// </remarks>
        internal void PrepareCompletionEvents()
        {
            RequireUsableSession("prepare completion events on");

            if (_completionEventsPrepareAttempted)
            {
                throw new InvalidOperationException(
                    "This session's completion-event preparation was already attempted; it is not attempted again.");
            }

            _completionEventsPrepareAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeCompletionEventResultV1 result = default;
            int written = ZantetsuNvencPrepareSessionCompletionEventsV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeCompletionEventResultV1)));

            RequireCompletionEventResult(written, result, "prepared");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _completionEventsPrepared = true;
        }

        /// <summary>
        /// Unregisters and closes that whole set.
        /// </summary>
        /// <remarks>
        /// Only a complete success clears it: a refused unregister or handle
        /// close leaves what it stopped at with the session, which therefore
        /// still cannot be closed, and nothing is retried here.
        /// </remarks>
        internal void ReleaseCompletionEvents()
        {
            RequireUsableSession("release completion events from");

            if (!_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session has no prepared completion events to release.");
            }

            if (_completionEventsReleaseAttempted)
            {
                throw new InvalidOperationException(
                    "This session's completion-event release was already attempted; it is not attempted again.");
            }

            _completionEventsReleaseAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeCompletionEventResultV1 result = default;
            int written = ZantetsuNvencReleaseSessionCompletionEventsV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeCompletionEventResultV1)));

            RequireCompletionEventResult(written, result, "released");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _completionEventsPrepared = false;
        }

        /// <summary>
        /// Creates this session's fixed set of NV12 input surfaces and
        /// registers them with the encoder - all of them, or none.
        /// </summary>
        /// <remarks>
        /// The textures and their registrations are the native side's: no
        /// pointer, handle, slot index, count, or descriptor is exposed here,
        /// and nothing is converted into or mapped from them yet. One owner
        /// prepares once, whatever the first attempt concluded, and a failure
        /// unwinds what it took.
        /// </remarks>
        internal void PrepareInputSurfaces()
        {
            RequireUsableSession("prepare input surfaces on");

            if (_inputSurfacesPrepareAttempted)
            {
                throw new InvalidOperationException(
                    "This session's input surface preparation was already attempted; it is not attempted again.");
            }

            _inputSurfacesPrepareAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeInputSurfaceResultV1 result = default;
            int written = ZantetsuNvencPrepareSessionInputSurfacesV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeInputSurfaceResultV1)));

            RequireInputSurfaceResult(written, result, "prepared");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _inputSurfacesPrepared = true;
        }

        /// <summary>
        /// Unregisters and releases that whole set.
        /// </summary>
        /// <remarks>
        /// The completion events and the output buffers are released first: a
        /// session that still has either refuses here, before this release's
        /// one attempt is spent. Only a complete success clears the set; a
        /// refused unregister leaves what it stopped at with the session, which
        /// therefore still cannot be closed, and nothing is retried here.
        /// </remarks>
        internal void ReleaseInputSurfaces()
        {
            RequireUsableSession("release input surfaces from");

            if (!_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session has no prepared input surfaces to release.");
            }

            // The resources prepared on top of the surfaces go first. Checked
            // before this release's one attempt is spent.
            if (_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's completion events are still prepared; they are released before its input surfaces.");
            }

            if (_outputBuffersPrepared)
            {
                throw new InvalidOperationException(
                    "This session's output buffers are still prepared; they are released before its input surfaces.");
            }

            if (_inputSurfacesReleaseAttempted)
            {
                throw new InvalidOperationException(
                    "This session's input surface release was already attempted; it is not attempted again.");
            }

            _inputSurfacesReleaseAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeInputSurfaceResultV1 result = default;
            int written = ZantetsuNvencReleaseSessionInputSurfacesV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeInputSurfaceResultV1)));

            RequireInputSurfaceResult(written, result, "released");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _inputSurfacesPrepared = false;
        }

        /// <summary>
        /// Creates this session's fixed set of output bitstream buffers - all
        /// of them, or none.
        /// </summary>
        /// <remarks>
        /// The buffers are the native side's: no pointer, slot index, count,
        /// or size is exposed here, and nothing is locked or read from them
        /// yet. One owner prepares once, whatever the first attempt concluded,
        /// and a failure destroys what it made.
        /// </remarks>
        internal void PrepareOutputBuffers()
        {
            RequireUsableSession("prepare output buffers on");

            // The input surfaces come first. Checked before this
            // preparation's one attempt is spent.
            if (!_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are not prepared; they come before its output buffers.");
            }

            if (_outputBuffersPrepareAttempted)
            {
                throw new InvalidOperationException(
                    "This session's output buffer preparation was already attempted; it is not attempted again.");
            }

            _outputBuffersPrepareAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeOutputBufferResultV1 result = default;
            int written = ZantetsuNvencPrepareSessionOutputBuffersV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeOutputBufferResultV1)));

            RequireOutputBufferResult(written, result, "prepared");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _outputBuffersPrepared = true;
        }

        /// <summary>
        /// Destroys that whole set.
        /// </summary>
        /// <remarks>
        /// The completion events are released first: a session that still has
        /// them refuses here, before this release's one attempt is spent, so
        /// the caller releases them and comes back. Only a complete success
        /// clears the set: a refused destroy leaves what it stopped at with
        /// the session, which therefore still cannot be closed, and nothing is
        /// retried here.
        /// </remarks>
        internal void ReleaseOutputBuffers()
        {
            RequireUsableSession("release output buffers from");

            if (!_outputBuffersPrepared)
            {
                throw new InvalidOperationException(
                    "This session has no prepared output buffers to release.");
            }

            // The completion events go first. Checked before this release's
            // one attempt is spent, so releasing them and then the buffers is
            // still the ordinary sequence.
            if (_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's completion events are still prepared; they are released before its output buffers.");
            }

            if (_outputBuffersReleaseAttempted)
            {
                throw new InvalidOperationException(
                    "This session's output buffer release was already attempted; it is not attempted again.");
            }

            _outputBuffersReleaseAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeOutputBufferResultV1 result = default;
            int written = ZantetsuNvencReleaseSessionOutputBuffersV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeOutputBufferResultV1)));

            RequireOutputBufferResult(written, result, "released");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _outputBuffersPrepared = false;
        }

        /// <summary>
        /// Closes the session once. A refused close leaves this owner holding
        /// the same still-open session and spends the attempt, so disposing
        /// again throws instead of asking the native side to destroy that
        /// encoder a second time.
        /// </summary>
        public void Dispose()
        {
            if (_sessionOwner == 0)
            {
                return;
            }

            // Prepared completion events and output buffers hold this session
            // open. Refused before the close attempt is spent, so the caller
            // can still close after releasing them.
            if (_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's completion events are still prepared; they are released before the session is closed.");
            }

            if (_outputBuffersPrepared)
            {
                throw new InvalidOperationException(
                    "This session's output buffers are still prepared; they are released before the session is closed.");
            }

            if (_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are still prepared; they are released before the session is closed.");
            }

            // One owner, one close attempt - settled before the native side is
            // called, so a destroy it refused is never asked for again.
            if (_closeAttempted)
            {
                throw new InvalidOperationException(
                    "This encoder session's close was already attempted and refused; it is not closed again.");
            }

            _closeAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeCloseResultV1 result = default;
            int written = ZantetsuNvencCloseSessionV1(
                _sessionOwner, ref result, (uint)Marshal.SizeOf(typeof(NativeCloseResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native encoder session close was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The native encoder session could not be closed (status "
                    + result.Status + ", win32 error " + result.LastWin32Error
                    + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }
#endif

            _sessionOwner = 0;
        }

        private void RequireUsableSession(string what)
        {
            if (_sessionOwner == 0)
            {
                throw new InvalidOperationException(
                    "This encoder session is not open; there is nothing to " + what + ".");
            }

            if (_closeAttempted)
            {
                throw new InvalidOperationException(
                    "This encoder session's close was already attempted; it is too late to "
                    + what + " it.");
            }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static void RequireCompletionEventResult(
            int written, NativeCompletionEventResultV1 result, string what)
        {
            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native completion-event call was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The completion events could not be " + what + " (status "
                    + result.Status + ", win32 error " + result.LastWin32Error
                    + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }
        }

        private static void RequireInputSurfaceResult(
            int written, NativeInputSurfaceResultV1 result, string what)
        {
            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native input-surface call was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The input surfaces could not be " + what + " (status "
                    + result.Status + ", HRESULT 0x" + result.LastHResult.ToString("X8")
                    + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }
        }

        private static void RequireOutputBufferResult(
            int written, NativeOutputBufferResultV1 result, string what)
        {
            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native output-buffer call was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The output buffers could not be " + what + " (status "
                    + result.Status + ", NVENCSTATUS " + result.LastNvencStatus + ").");
            }
        }

        private static bool ToBoolean(uint value, string name)
        {
            if (value > 1)
            {
                throw new InvalidOperationException(
                    "The native encoder capability observation returned " + value
                    + " for " + name + "; the ABI allows only zero or one.");
            }

            return value == 1;
        }

        private static void RequireAbiVersion(uint abiVersion)
        {
            if (abiVersion != AbiVersion)
            {
                throw new InvalidOperationException(
                    "The native encoder session ABI is version " + abiVersion
                    + "; this build speaks version " + AbiVersion + ".");
            }
        }
#endif
    }
}
