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
        private static extern int ZantetsuNvencCloseSessionV1(
            ulong sessionOwner, ref NativeCloseResultV1 destination, uint destinationSize);
#endif

        private ulong _sessionOwner;
        private bool _closeAttempted;

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

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
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
