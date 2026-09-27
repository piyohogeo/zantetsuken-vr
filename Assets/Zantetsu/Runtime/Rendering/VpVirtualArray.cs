using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Where the pages of a <see cref="VpVirtualArray{T}"/> come from: an address range reserved once and pages
    /// committed into it on demand (DESIGN 4.5.4). A reservation is address space only; committing is what takes
    /// memory, and a reservation that succeeded does not promise that a later commit will.
    /// <para>
    /// The product's backing is <see cref="VpWindowsPageBacking"/>. Another backing exists to let a test make a reserve
    /// or a commit fail on purpose, without exhausting real memory.
    /// </para>
    /// </summary>
    public interface IVpPageBacking
    {
        /// <summary>The unit a reservation and a commit are rounded up to, in bytes.</summary>
        long Granularity { get; }

        /// <summary>Reserves <paramref name="bytes"/> of address space, none of it committed. False, reserving nothing, on failure.</summary>
        bool TryReserve(long bytes, out IntPtr address, out string failure);

        /// <summary>
        /// Commits <paramref name="bytes"/> from <paramref name="address"/>, inside a reservation of this backing and
        /// on its granularity. Committing pages already committed leaves their contents as they are. False on failure.
        /// </summary>
        bool TryCommit(IntPtr address, long bytes, out string failure);

        /// <summary>Gives a whole reservation back, committed pages and all. Called once per reservation.</summary>
        void Release(IntPtr address, long bytes);
    }

    /// <summary>
    /// VirtualAlloc on Windows x64: MEM_RESERVE with no access for the range, MEM_COMMIT with read/write for the pages
    /// in use, and MEM_RELEASE of the whole range at the end. Commit is rounded to 64 KiB (the allocation granularity),
    /// so a commit is always a whole number of pages.
    /// </summary>
    public sealed class VpWindowsPageBacking : IVpPageBacking
    {
        public static readonly VpWindowsPageBacking Instance = new VpWindowsPageBacking();

        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageNoAccess = 0x01;
        private const uint PageReadWrite = 0x04;

        private VpWindowsPageBacking()
        {
        }

        public long Granularity => 64 * 1024;

        public bool TryReserve(long bytes, out IntPtr address, out string failure)
        {
            address = IntPtr.Zero;
            if (!IsSupported(out failure))
            {
                return false;
            }

            address = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)bytes, MemReserve, PageNoAccess);
            if (address == IntPtr.Zero)
            {
                failure = "VirtualAlloc reserve of " + bytes + " bytes failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            failure = null;
            return true;
        }

        public bool TryCommit(IntPtr address, long bytes, out string failure)
        {
            if (VirtualAlloc(address, (UIntPtr)(ulong)bytes, MemCommit, PageReadWrite) == IntPtr.Zero)
            {
                failure = "VirtualAlloc commit of " + bytes + " bytes failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            failure = null;
            return true;
        }

        public void Release(IntPtr address, long bytes)
        {
            // MEM_RELEASE takes the whole reservation and a size of 0.
            VirtualFree(address, UIntPtr.Zero, MemRelease);
        }

        private static bool IsSupported(out string failure)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (IntPtr.Size == 8)
            {
                failure = null;
                return true;
            }
#endif
            failure = "the VP storage's virtual backing is Windows x64 only";
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
    }

    /// <summary>
    /// One linear array of <typeparamref name="T"/> in a reserved address range whose pages are committed on demand
    /// (DESIGN 4.5.3, 4.5.4): the base never moves, so growing the committed part copies nothing and every view,
    /// pointer and span handed out before stays valid.
    /// <para>
    /// <see cref="View"/> is a NativeArray over the whole reservation, made over memory this object owns
    /// (<c>ConvertExistingDataToNativeArray</c>, <see cref="Allocator.None"/>): it is never disposed by its users, and
    /// this object's <see cref="Dispose"/> ends its safety handle and gives the range back, once. Only
    /// [0, <see cref="CommittedLength"/>) may be read or written; the owner commits a range before handing it out, and
    /// nothing past it is ever given to a reader, a worker or a transfer.
    /// </para>
    /// <para>
    /// Sizes and offsets are 64-bit; the element count of the view is at most int.MaxValue, and the byte count is not
    /// confused with it. The main thread alone grows and disposes it.
    /// </para>
    /// </summary>
    public sealed unsafe class VpVirtualArray<T> : IDisposable where T : unmanaged
    {
        /// <summary>
        /// A commit that grows doubles what is committed, so that a run of small appends does not commit page by page,
        /// but by no more than this in one step, so that a large commit does not jump further than it needs to; it is
        /// always at least what was asked, rounded to the backing's granularity, and never past the reservation.
        /// </summary>
        public const long MaximumGrowthStepBytes = 64L << 20;

        private readonly IVpPageBacking _backing;
        private IntPtr _base;
        private NativeArray<T> _view;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private AtomicSafetyHandle _safety;
#endif
        private bool _disposed;

        private VpVirtualArray(IVpPageBacking backing, IntPtr address, int reservedLength, long reservedBytes)
        {
            _backing = backing;
            _base = address;
            ReservedLength = reservedLength;
            ReservedBytes = reservedBytes;
            _view = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>((void*)address, reservedLength, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            _safety = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref _view, _safety);
#endif
        }

        /// <summary>
        /// Reserves room for <paramref name="reservedLength"/> elements and commits the first
        /// <paramref name="initialCommittedLength"/>. False, holding nothing, when the lengths do not hold together or
        /// the backing refuses the reservation or the first commit; <paramref name="failure"/> says which.
        /// </summary>
        public static bool TryCreate(
            IVpPageBacking backing, int reservedLength, int initialCommittedLength, out VpVirtualArray<T> array, out string failure)
        {
            array = null;
            if (backing == null)
            {
                throw new ArgumentNullException(nameof(backing));
            }

            if (reservedLength < 0 || initialCommittedLength < 0 || initialCommittedLength > reservedLength)
            {
                failure = "reserved " + reservedLength + " and initially committed " + initialCommittedLength + " elements do not hold together";
                return false;
            }

            long reservedBytes = RoundUp(Math.Max(1L, (long)reservedLength * sizeof(T)), backing.Granularity);
            if (!backing.TryReserve(reservedBytes, out IntPtr address, out failure))
            {
                return false;
            }

            var made = new VpVirtualArray<T>(backing, address, reservedLength, reservedBytes);
            if (!made.TryCommitTo(initialCommittedLength, out failure))
            {
                made.Dispose();
                return false;
            }

            array = made;
            failure = null;
            return true;
        }

        /// <summary>How many elements the reservation holds: the absolute limit of this array.</summary>
        public int ReservedLength { get; }

        public long ReservedBytes { get; }

        /// <summary>How many elements from the base are committed and may be used.</summary>
        public int CommittedLength { get; private set; }

        public long CommittedBytes { get; private set; }

        /// <summary>The view over the whole reservation, of which only [0, CommittedLength) may be touched. Never disposed by its users.</summary>
        public NativeArray<T> View
        {
            get
            {
                ThrowIfDisposed();
                return _view;
            }
        }

        /// <summary>
        /// Makes [0, <paramref name="length"/>) committed, growing the commit when it is short: to at least what is
        /// asked, by doubling where that stays within one growth step, and never past the reservation. Nothing moves
        /// and nothing is copied. True at once when it is already committed. False, committing nothing further, when
        /// <paramref name="length"/> lies beyond the reservation (the absolute limit) or the backing refuses the pages.
        /// </summary>
        public bool TryCommitTo(int length, out string failure)
        {
            ThrowIfDisposed();
            failure = null;
            if (length <= CommittedLength)
            {
                return length >= 0;
            }

            if (length > ReservedLength)
            {
                failure = "the reservation of " + ReservedLength + " elements (" + ReservedBytes + " bytes) is the limit and " + length + " were asked for";
                return false;
            }

            long needed = RoundUp((long)length * sizeof(T), _backing.Granularity);
            long grown = Math.Min(CommittedBytes * 2, CommittedBytes + MaximumGrowthStepBytes);
            long target = Math.Min(ReservedBytes, RoundUp(Math.Max(needed, grown), _backing.Granularity));
            if (!_backing.TryCommit((IntPtr)((byte*)_base + CommittedBytes), target - CommittedBytes, out failure))
            {
                return false;
            }

            CommittedBytes = target;
            CommittedLength = (int)Math.Min(ReservedLength, target / sizeof(T));
            return true;
        }

        /// <summary>
        /// Ends the view's safety handle and gives the whole range back, once. A job still using the view makes this
        /// throw, as disposing an ordinary NativeArray does, and then nothing is released. Disposing again does nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(_safety);
            AtomicSafetyHandle.Release(_safety);
#endif
            _disposed = true;
            _view = default;
            IntPtr address = _base;
            _base = IntPtr.Zero;
            _backing.Release(address, ReservedBytes);
        }

        private static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpVirtualArray<T>));
            }
        }
    }
}
