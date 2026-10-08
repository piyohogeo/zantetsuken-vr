using System;
using System.Runtime.InteropServices;

namespace Zantetsu.Rendering
{
    /// <summary>Selects the OS backing; every default storage constructor goes through this factory.</summary>
    public static class VpPlatformPageBacking
    {
        public static IVpPageBacking Instance
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return VpAndroidPageBacking.Instance;
#else
                return VpWindowsPageBacking.Instance;
#endif
            }
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>Stable virtual addresses on Android ARM64. Pages become accessible on demand; the OS backs them lazily.</summary>
    public sealed class VpAndroidPageBacking : IVpPageBacking
    {
        public static readonly VpAndroidPageBacking Instance = new VpAndroidPageBacking();
        private readonly long _pageSize = getpagesize();
        public long Granularity => _pageSize;

        public bool TryReserve(long bytes, out IntPtr address, out string failure)
        {
            address = mmap(IntPtr.Zero, (UIntPtr)(ulong)bytes, 0, 0x02 | 0x20, -1, 0);
            if (address == new IntPtr(-1))
            {
                address = IntPtr.Zero;
                failure = "mmap reserve of " + bytes + " bytes failed (errno " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            failure = null;
            return true;
        }

        public bool TryCommit(IntPtr address, long bytes, out string failure)
        {
            if (mprotect(address, (UIntPtr)(ulong)bytes, 1 | 2) != 0)
            {
                failure = "mprotect commit of " + bytes + " bytes failed (errno " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            failure = null;
            return true;
        }

        public void Release(IntPtr address, long bytes)
        {
            if (munmap(address, (UIntPtr)(ulong)bytes) != 0)
                throw new InvalidOperationException("munmap failed (errno " + Marshal.GetLastWin32Error() + ")");
        }

        [DllImport("libc", SetLastError = true)] private static extern IntPtr mmap(IntPtr address, UIntPtr length, int protection, int flags, int fd, long offset);
        [DllImport("libc", SetLastError = true)] private static extern int mprotect(IntPtr address, UIntPtr length, int protection);
        [DllImport("libc", SetLastError = true)] private static extern int munmap(IntPtr address, UIntPtr length);
        [DllImport("libc")] private static extern int getpagesize();
    }
#endif
}
