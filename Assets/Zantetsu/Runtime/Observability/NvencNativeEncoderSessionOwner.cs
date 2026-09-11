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
    /// this owner's; it is not retried here, and there is no finalizer, safe
    /// handle, registry, or generation counter behind it.
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
        private static extern int ZantetsuNvencCloseSessionV1(
            ulong sessionOwner, ref NativeCloseResultV1 destination, uint destinationSize);
#endif

        private ulong _sessionOwner;

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
        /// Closes the session once. A refused close leaves this owner holding
        /// the same still-open session.
        /// </summary>
        public void Dispose()
        {
            if (_sessionOwner == 0)
            {
                return;
            }

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
