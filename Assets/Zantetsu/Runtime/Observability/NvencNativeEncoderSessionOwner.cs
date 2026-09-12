using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

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

        /// <summary>
        /// How many source surfaces one session binds. Fixed, and its own
        /// count: a source surface and an encode sample slot are two different
        /// sets.
        /// </summary>
        internal const int SourceSurfaceCount = 8;

        /// <summary>
        /// How many GPU conversion command slots one session has. Fixed, and
        /// its own count: a command binds one source to one encode sample slot
        /// for one frame.
        /// </summary>
        internal const int ConversionCommandSlotCount = 8;

        /// <summary>
        /// How many encode sample slots one session prepares. Fixed, and its
        /// own count: a sample slot holds an NV12 input surface, an output
        /// bitstream buffer, and a completion event.
        /// </summary>
        internal const int EncodeSampleSlotCount = 8;

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
        private struct NativeSourceSurfaceRequestV1
        {
            internal uint AbiVersion;
            internal uint SurfaceCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = SourceSurfaceCount)]
            internal ulong[] Surfaces;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSourceSurfaceResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastHResult;
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
        private struct NativeConversionResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastHResult;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeConversionArmRequestV1
        {
            internal uint AbiVersion;
            internal uint SyncSlotIndex;
            internal uint SourceSlotIndex;
            internal uint SampleSlotIndex;
            internal ulong Generation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeConversionArmResultV1
        {
            internal uint AbiVersion;
            internal uint Status;
            internal int LastHResult;
            internal int Reserved;
            internal ulong EventData;
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
        private static extern int ZantetsuNvencBindSessionSourceSurfacesV1(
            ulong sessionOwner, ref NativeSourceSurfaceRequestV1 request,
            uint requestSize, ref NativeSourceSurfaceResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencReleaseSessionSourceSurfacesV1(
            ulong sessionOwner, ref NativeSourceSurfaceResultV1 destination,
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
        private static extern int ZantetsuNvencPrepareSessionConversionCommandsV1(
            ulong sessionOwner, ref NativeConversionResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencReleaseSessionConversionCommandsV1(
            ulong sessionOwner, ref NativeConversionResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern ulong ZantetsuNvencGetConversionEventCallbackV1();

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencArmSessionConversionCommandV1(
            ulong sessionOwner, ref NativeConversionArmRequestV1 request,
            uint requestSize, ref NativeConversionArmResultV1 destination,
            uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencCancelSessionConversionCommandV1(
            ulong sessionOwner, uint syncSlotIndex, ulong generation,
            ref NativeConversionResultV1 destination, uint destinationSize);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencCollectSessionConversionCommandV1(
            ulong sessionOwner, uint syncSlotIndex, ulong generation,
            uint timeoutMilliseconds, ref NativeConversionResultV1 destination,
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
        // The render callback this plugin wants a conversion issued with,
        // asked for once and kept here. Neither it nor an event data pointer
        // is ever handed to a caller.
        private IntPtr _conversionEventCallback;
        // How many issued conversions have not been collected yet. Kept here
        // so a release is refused before its one attempt is spent, the way
        // every other ordering refusal is.
        private int _conversionCommandsInFlight;
        private bool _conversionCommandsPrepareAttempted;
        private bool _conversionCommandsReleaseAttempted;
        private bool _conversionCommandsPrepared;
        private bool _outputBuffersPrepareAttempted;
        private bool _outputBuffersReleaseAttempted;
        private bool _outputBuffersPrepared;
        private bool _sourceSurfacesBindAttempted;
        private bool _sourceSurfacesReleaseAttempted;
        private bool _sourceSurfacesBound;
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
        /// Binds this session's fixed set of source RGBA surfaces - all of
        /// them, or none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pointers name textures the pool owns and goes on owning. This
        /// owner neither holds nor disposes them; the native session takes
        /// only its own COM reference to each, plus the view it will read
        /// through, and none of that comes back: no pointer, view, descriptor,
        /// slot, or count is returned.
        /// </para>
        /// <para>
        /// A null set, a set that is not the fixed size, or a null pointer in
        /// it is refused here, before the one binding attempt is spent. One
        /// owner binds once, whatever the first attempt concluded.
        /// </para>
        /// </remarks>
        internal void BindSourceSurfaces(IntPtr[] sourceTextures)
        {
            RequireUsableSession("bind source surfaces to");

            if (sourceTextures == null)
            {
                throw new ArgumentNullException(nameof(sourceTextures));
            }

            if (sourceTextures.Length != SourceSurfaceCount)
            {
                throw new ArgumentException(
                    "The source surface set must be exactly " + SourceSurfaceCount
                    + " textures.", nameof(sourceTextures));
            }

            for (int i = 0; i < sourceTextures.Length; i++)
            {
                if (sourceTextures[i] == IntPtr.Zero)
                {
                    throw new ArgumentException(
                        "The source surface set contains a null texture.",
                        nameof(sourceTextures));
                }
            }

            if (_sourceSurfacesBindAttempted)
            {
                throw new InvalidOperationException(
                    "This session's source surface binding was already attempted; it is not attempted again.");
            }

            _sourceSurfacesBindAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeSourceSurfaceRequestV1 request = new NativeSourceSurfaceRequestV1
            {
                AbiVersion = AbiVersion,
                SurfaceCount = (uint)SourceSurfaceCount,
                Surfaces = new ulong[SourceSurfaceCount],
            };

            for (int i = 0; i < sourceTextures.Length; i++)
            {
                request.Surfaces[i] = (ulong)sourceTextures[i].ToInt64();
            }

            NativeSourceSurfaceResultV1 result = default;
            int written = ZantetsuNvencBindSessionSourceSurfacesV1(
                _sessionOwner, ref request,
                (uint)Marshal.SizeOf(typeof(NativeSourceSurfaceRequestV1)),
                ref result,
                (uint)Marshal.SizeOf(typeof(NativeSourceSurfaceResultV1)));

            RequireSourceSurfaceResult(written, result, "bound");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _sourceSurfacesBound = true;
        }

        /// <summary>
        /// Releases that whole set - each view, then each reference.
        /// </summary>
        /// <remarks>
        /// Everything prepared on top of the sources is released first: a
        /// session that still has its NV12 input surfaces, output buffers, or
        /// completion events refuses here, before this release's one attempt is
        /// spent. The textures themselves are untouched; only what the native
        /// side took is given back.
        /// </remarks>
        internal void ReleaseSourceSurfaces()
        {
            RequireUsableSession("release source surfaces from");

            if (!_sourceSurfacesBound)
            {
                throw new InvalidOperationException(
                    "This session has no bound source surfaces to release.");
            }

            // The resources prepared on top of the sources go first. Checked
            // before this release's one attempt is spent.
            if (_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's completion events are still prepared; they are released before its source surfaces.");
            }

            if (_outputBuffersPrepared)
            {
                throw new InvalidOperationException(
                    "This session's output buffers are still prepared; they are released before its source surfaces.");
            }

            if (_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's conversion commands are still prepared; they are released before its source surfaces.");
            }

            if (_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are still prepared; they are released before its source surfaces.");
            }

            if (_sourceSurfacesReleaseAttempted)
            {
                throw new InvalidOperationException(
                    "This session's source surface release was already attempted; it is not attempted again.");
            }

            _sourceSurfacesReleaseAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeSourceSurfaceResultV1 result = default;
            int written = ZantetsuNvencReleaseSessionSourceSurfacesV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeSourceSurfaceResultV1)));

            RequireSourceSurfaceResult(written, result, "released");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _sourceSurfacesBound = false;
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

            // The source surfaces come first. Checked before this
            // preparation's one attempt is spent.
            if (!_sourceSurfacesBound)
            {
                throw new InvalidOperationException(
                    "This session's source surfaces are not bound; they come before its input surfaces.");
            }

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

            if (_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's conversion commands are still prepared; they are released before its input surfaces.");
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
        /// Creates this session's fixed set of GPU conversion command slots -
        /// all of them, or none.
        /// </summary>
        /// <remarks>
        /// The fences, their events, and the event data each command is issued
        /// with are the native side's; no handle, pointer, or slot state is
        /// exposed here. The source surfaces, the NV12 input surfaces, and the
        /// conversion pipeline come first; the output buffers and completion
        /// events come after.
        /// </remarks>
        internal void PrepareConversionCommands()
        {
            RequireUsableSession("prepare conversion commands on");

            // The surfaces a conversion reads and writes come first. Checked
            // before this preparation's one attempt is spent.
            if (!_sourceSurfacesBound)
            {
                throw new InvalidOperationException(
                    "This session's source surfaces are not bound; they come before its conversion commands.");
            }

            if (!_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are not prepared; they come before its conversion commands.");
            }

            if (_conversionCommandsPrepareAttempted)
            {
                throw new InvalidOperationException(
                    "This session's conversion command preparation was already attempted; it is not attempted again.");
            }

            _conversionCommandsPrepareAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeConversionResultV1 result = default;
            int written = ZantetsuNvencPrepareSessionConversionCommandsV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeConversionResultV1)));

            RequireConversionResult(written, result, "prepared");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _conversionCommandsPrepared = true;
        }

        /// <summary>
        /// Releases that whole set.
        /// </summary>
        /// <remarks>
        /// The output buffers and completion events are released first, and no
        /// command may still be in flight: a session that fails either refuses
        /// here, before this release's one attempt is spent.
        /// </remarks>
        internal void ReleaseConversionCommands()
        {
            RequireUsableSession("release conversion commands from");

            if (!_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session has no prepared conversion commands to release.");
            }

            if (_completionEventsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's completion events are still prepared; they are released before its conversion commands.");
            }

            if (_outputBuffersPrepared)
            {
                throw new InvalidOperationException(
                    "This session's output buffers are still prepared; they are released before its conversion commands.");
            }

            // A command that has been issued and not collected is still the
            // render thread's. Checked before this release's one attempt is
            // spent, so the caller can still release once it is collected.
            int inFlight = System.Threading.Volatile.Read(
                ref _conversionCommandsInFlight);
            if (inFlight != 0)
            {
                throw new InvalidOperationException(
                    "This session still has "
                    + inFlight
                    + " uncollected conversion command(s); they are collected before the set is released.");
            }

            if (_conversionCommandsReleaseAttempted)
            {
                throw new InvalidOperationException(
                    "This session's conversion command release was already attempted; it is not attempted again.");
            }

            _conversionCommandsReleaseAttempted = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeConversionResultV1 result = default;
            int written = ZantetsuNvencReleaseSessionConversionCommandsV1(
                _sessionOwner, ref result,
                (uint)Marshal.SizeOf(typeof(NativeConversionResultV1)));

            RequireConversionResult(written, result, "released");
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif

            _conversionCommandsPrepared = false;
        }

        /// <summary>
        /// Arms one conversion and issues its render event: source surface,
        /// encode sample slot, sync slot, and generation, bound together in one
        /// call on the main thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The event data the native side hands back lives in that command's
        /// slot and is read by the render callback when it runs, so it is
        /// passed straight to the render event and never kept, copied, wrapped,
        /// or shown to anyone here. Neither it nor the callback address leaves
        /// this type.
        /// </para>
        /// <para>
        /// Only a failure to issue the event unwinds the arming: the command is
        /// taken back, and if its callback has already started it is kept
        /// instead, because it will signal and must be collected.
        /// </para>
        /// </remarks>
        internal void IssueConversionCommand(
            int syncSlotIndex, int sourceSlotIndex, int sampleSlotIndex, ulong generation)
        {
            RequireUsableSession("issue a conversion command on");

            if (!_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's conversion commands are not prepared; there is nothing to issue.");
            }

            if (syncSlotIndex < 0 || syncSlotIndex >= ConversionCommandSlotCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(syncSlotIndex), syncSlotIndex,
                    "The sync slot index is outside the fixed set.");
            }

            if (sourceSlotIndex < 0 || sourceSlotIndex >= SourceSurfaceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSlotIndex), sourceSlotIndex,
                    "The source slot index is outside the fixed set.");
            }

            if (sampleSlotIndex < 0 || sampleSlotIndex >= EncodeSampleSlotCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleSlotIndex), sampleSlotIndex,
                    "The encode sample slot index is outside the fixed set.");
            }

            if (generation == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generation), generation,
                    "A generation is greater than zero.");
            }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeConversionArmRequestV1 request = new NativeConversionArmRequestV1
            {
                AbiVersion = AbiVersion,
                SyncSlotIndex = (uint)syncSlotIndex,
                SourceSlotIndex = (uint)sourceSlotIndex,
                SampleSlotIndex = (uint)sampleSlotIndex,
                Generation = generation,
            };

            NativeConversionArmResultV1 armed = default;
            int written = ZantetsuNvencArmSessionConversionCommandV1(
                _sessionOwner, ref request,
                (uint)Marshal.SizeOf(typeof(NativeConversionArmRequestV1)),
                ref armed,
                (uint)Marshal.SizeOf(typeof(NativeConversionArmResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native conversion arming was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(armed.AbiVersion);

            if (armed.Status != StatusOk || armed.EventData == 0)
            {
                throw new InvalidOperationException(
                    "The conversion command could not be armed (status "
                    + armed.Status + ", HRESULT 0x"
                    + armed.LastHResult.ToString("X8") + ").");
            }

            if (_conversionEventCallback == IntPtr.Zero)
            {
                _conversionEventCallback =
                    new IntPtr((long)ZantetsuNvencGetConversionEventCallbackV1());

                if (_conversionEventCallback == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "The native side reported no conversion render callback.");
                }
            }

            try
            {
                CommandBuffer commands = new CommandBuffer();
                try
                {
                    commands.IssuePluginEventAndData(
                        _conversionEventCallback,
                        0,
                        new IntPtr((long)armed.EventData));
                    Graphics.ExecuteCommandBuffer(commands);
                }
                finally
                {
                    commands.Dispose();
                }
            }
            catch (Exception)
            {
                // The event never reached the render thread, so the command is
                // taken back - unless its callback has already started, in
                // which case it stays armed to be collected and still counts
                // as in flight.
                NativeConversionResultV1 cancelled = default;
                int cancelWritten = ZantetsuNvencCancelSessionConversionCommandV1(
                    _sessionOwner, (uint)syncSlotIndex, generation, ref cancelled,
                    (uint)Marshal.SizeOf(typeof(NativeConversionResultV1)));

                if (cancelWritten != 1 || cancelled.Status != StatusOk)
                {
                    System.Threading.Interlocked.Increment(
                        ref _conversionCommandsInFlight);
                }

                throw;
            }

            System.Threading.Interlocked.Increment(ref _conversionCommandsInFlight);
#else
            throw new InvalidOperationException(
                "The native encoder session is not available on this platform.");
#endif
        }

        /// <summary>
        /// Waits for exactly one issued conversion to complete and returns its
        /// slot to use. Returns false when it did not complete in time or the
        /// callback recorded a failure.
        /// </summary>
        /// <remarks>
        /// For a worker thread: it touches the command's fence and its event
        /// and nothing else - no Unity API, no graphics context, no drawing.
        /// An older generation, a slot with nothing outstanding, and a second
        /// collection are all refused by the native side.
        /// </remarks>
        internal bool TryCollectConversionCommand(
            int syncSlotIndex, ulong generation, uint timeoutMilliseconds)
        {
            if (_sessionOwner == 0 || _closeAttempted)
            {
                return false;
            }

            if (syncSlotIndex < 0 || syncSlotIndex >= ConversionCommandSlotCount)
            {
                return false;
            }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeConversionResultV1 result = default;
            int written = ZantetsuNvencCollectSessionConversionCommandV1(
                _sessionOwner, (uint)syncSlotIndex, generation, timeoutMilliseconds,
                ref result, (uint)Marshal.SizeOf(typeof(NativeConversionResultV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native conversion collection was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                return false;
            }

            // Collected: this one is no longer the render thread's.
            System.Threading.Interlocked.Decrement(ref _conversionCommandsInFlight);
            return true;
#else
            return false;
#endif
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

            // The input surfaces and the conversion commands come first.
            // Checked before this preparation's one attempt is spent.
            if (!_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are not prepared; they come before its output buffers.");
            }

            if (!_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's conversion commands are not prepared; they come before its output buffers.");
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

            if (_conversionCommandsPrepared)
            {
                throw new InvalidOperationException(
                    "This session's conversion commands are still prepared; they are released before the session is closed.");
            }

            if (_inputSurfacesPrepared)
            {
                throw new InvalidOperationException(
                    "This session's input surfaces are still prepared; they are released before the session is closed.");
            }

            if (_sourceSurfacesBound)
            {
                throw new InvalidOperationException(
                    "This session's source surfaces are still bound; they are released before the session is closed.");
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

        private static void RequireConversionResult(
            int written, NativeConversionResultV1 result, string what)
        {
            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native conversion-command call was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The conversion commands could not be " + what + " (status "
                    + result.Status + ", HRESULT 0x"
                    + result.LastHResult.ToString("X8") + ").");
            }
        }

        private static void RequireSourceSurfaceResult(
            int written, NativeSourceSurfaceResultV1 result, string what)
        {
            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native source-surface call was refused; it returned "
                    + written + ".");
            }

            RequireAbiVersion(result.AbiVersion);

            if (result.Status != StatusOk)
            {
                throw new InvalidOperationException(
                    "The source surfaces could not be " + what + " (status "
                    + result.Status + ", HRESULT 0x"
                    + result.LastHResult.ToString("X8") + ").");
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
