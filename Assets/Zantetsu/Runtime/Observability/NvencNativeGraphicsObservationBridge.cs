using System;
using System.Runtime.InteropServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The managed side of the native graphics observation ABI: one call into
    /// the plugin, one snapshot back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The native entry point exists only in the Windows Standalone Player, and
    /// so does the declaration below: in the Editor and on every other platform
    /// this type never names the library, so that process cannot load it, and
    /// <see cref="TryObserve"/> reports plainly that nothing was observed
    /// rather than inventing a snapshot.
    /// </para>
    /// <para>
    /// Anything the ABI promises but does not deliver is a broken contract, not
    /// an unsupported platform: a refused write, an ABI version other than the
    /// one this build speaks, or a boolean that is neither zero nor one throws.
    /// A missing library or entry point throws too - those are the loader's own
    /// exceptions and are never folded into "unsupported", because a Player
    /// that was built with this plugin has to have it.
    /// </para>
    /// <para>
    /// One call is one observation. Nothing is retried, cached, polled, or kept
    /// between calls, and no device pointer is held, since the ABI carries
    /// none.
    /// </para>
    /// </remarks>
    internal static class NvencNativeGraphicsObservationBridge
    {
        /// <summary>The ABI version this build speaks.</summary>
        internal const uint AbiVersion = 1;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const string NativeLibraryName = "ZantetsuNvenc";

        /// <summary>
        /// The ABI's three fixed-width values. Booleans are zero or one.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeGraphicsObservationV1
        {
            internal uint AbiVersion;
            internal uint IsUnityPluginLoaded;
            internal uint HasCurrentD3D11Device;
        }

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZantetsuNvencGetGraphicsObservationV1(
            ref NativeGraphicsObservationV1 destination, uint destinationSize);
#endif

        /// <summary>
        /// True where the native plugin exists and may be called: the Windows
        /// Standalone Player, and nowhere else.
        /// </summary>
        internal static bool IsObservable
        {
            get
            {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Takes one observation. Returns false with an uninitialized snapshot
        /// where the plugin cannot be called at all; otherwise returns the
        /// snapshot the plugin wrote, or throws if it broke the ABI.
        /// </summary>
        internal static bool TryObserve(out NvencGraphicsObservationV1 observation)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            NativeGraphicsObservationV1 native = default;
            int written = ZantetsuNvencGetGraphicsObservationV1(
                ref native, (uint)Marshal.SizeOf(typeof(NativeGraphicsObservationV1)));

            if (written != 1)
            {
                throw new InvalidOperationException(
                    "The native graphics observation was refused; it returned "
                    + written + ".");
            }

            if (native.AbiVersion != AbiVersion)
            {
                throw new InvalidOperationException(
                    "The native graphics observation ABI is version "
                    + native.AbiVersion + "; this build speaks version "
                    + AbiVersion + ".");
            }

            observation = new NvencGraphicsObservationV1(
                ToBoolean(native.IsUnityPluginLoaded, "isUnityPluginLoaded"),
                ToBoolean(native.HasCurrentD3D11Device, "hasCurrentD3D11Device"));
            return true;
#else
            observation = default;
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static bool ToBoolean(uint value, string name)
        {
            if (value > 1)
            {
                throw new InvalidOperationException(
                    "The native graphics observation returned " + value + " for "
                    + name + "; the ABI allows only zero or one.");
            }

            return value == 1;
        }
#endif
    }
}
