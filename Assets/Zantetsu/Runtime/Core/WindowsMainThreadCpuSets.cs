using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zantetsu.Core
{
    /// <summary>
    /// The Windows side of <see cref="MainThreadCpuSetPlacement"/>: the CPU Set calls, and only those.
    /// <para>
    /// **One call writes; everything else reads.** The only thing set here is the **calling** thread's own CPU Set
    /// assignment, through <c>SetThreadSelectedCpuSets</c>. <c>GetSystemCpuSetInformation</c> reads the topology,
    /// <c>GetThreadSelectedCpuSets</c> reads that thread's current assignment, and <c>GetProcessAffinityMask</c> and
    /// <c>GetThreadGroupAffinity</c> read what the process and that thread are allowed — which has to be consulted,
    /// because a restrictive affinity mask is respected above any CPU Set assignment. Nothing here sets a hard
    /// affinity, nothing sets the process default CPU Sets, nothing touches a priority, and nothing takes a handle to
    /// another thread: <c>GetCurrentThread</c> is a pseudo handle for the caller itself.
    /// </para>
    /// <para>
    /// **Layout and meaning are the documented ones.** A <c>SYSTEM_CPU_SET_INFORMATION</c> record is
    /// <c>Size</c> and <c>Type</c> followed by <c>Id</c>, <c>Group</c>, <c>LogicalProcessorIndex</c>,
    /// <c>CoreIndex</c>, <c>LastLevelCacheIndex</c>, <c>NumaNodeIndex</c>, <c>EfficiencyClass</c> and the flag byte;
    /// the structure is variable-sized on purpose, so the walk steps by <c>Size</c> and skips records whose
    /// <c>Type</c> is not <c>CpuSetInformation</c>, the enumeration's only member and therefore zero. A higher
    /// <c>EfficiencyClass</c> is the faster, less power-efficient processor. Passing a null list to
    /// <c>SetThreadSelectedCpuSets</c> clears an assignment, which is how a thread that had none is put back.
    /// </para>
    /// </summary>
    public sealed class WindowsMainThreadCpuSets : IMainThreadCpuSetOs
    {
        /// <summary>The only member of <c>CPU_SET_INFORMATION_TYPE</c>, and so zero.</summary>
        private const int CpuSetInformationType = 0;

        /// <summary>
        /// How much of a record this reads: through the flag byte at offset 19. The documented record is larger and
        /// may grow; a shorter one than this would not hold what is needed and is skipped.
        /// </summary>
        private const int MinimumRecordBytes = 20;

        /// <summary>A sanity bound on the topology buffer, so a nonsense length is refused rather than allocated.</summary>
        private const int MaximumTopologyBytes = 1 << 20;

        /// <summary>A sanity bound on how many CPU Set IDs one thread's assignment may name.</summary>
        private const int MaximumCpuSetIds = 1024;

        public int CurrentThreadId => Thread.CurrentThread.ManagedThreadId;

        public int ProcessorGroupCount => GetActiveProcessorGroupCount();

        public bool TryReadSystemCpuSets(List<CpuSetRecord> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();

            // A null buffer of length zero asks for the size. The process handle is only what decides the
            // AllocatedToTargetProcess flag, and it is this process.
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint required, GetCurrentProcess(), 0);
            if (required == 0 || required > MaximumTopologyBytes)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, required, out uint returned, GetCurrentProcess(), 0))
                {
                    return false;
                }

                if (returned == 0 || returned > required)
                {
                    return false;
                }

                long offset = 0;
                while (offset + 8 <= returned)
                {
                    int size = Marshal.ReadInt32(buffer, (int)offset);
                    if (size <= 0 || offset + size > returned)
                    {
                        // A walk that does not add up is a failure to read, not a partial answer to act on.
                        into.Clear();
                        return false;
                    }

                    int type = Marshal.ReadInt32(buffer, (int)(offset + 4));
                    if (type == CpuSetInformationType && size >= MinimumRecordBytes)
                    {
                        int at = (int)offset + 8;
                        into.Add(new CpuSetRecord(
                            unchecked((uint)Marshal.ReadInt32(buffer, at)),
                            unchecked((ushort)Marshal.ReadInt16(buffer, at + 4)),
                            Marshal.ReadByte(buffer, at + 6),
                            Marshal.ReadByte(buffer, at + 7),
                            Marshal.ReadByte(buffer, at + 8),
                            Marshal.ReadByte(buffer, at + 9),
                            Marshal.ReadByte(buffer, at + 10),
                            Marshal.ReadByte(buffer, at + 11)));
                    }

                    offset += size;
                }

                return into.Count > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool TryReadProcessAffinityMask(out ulong mask)
        {
            if (!GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr process, out UIntPtr _))
            {
                mask = 0;
                return false;
            }

            mask = (ulong)process;
            return mask != 0;
        }

        public bool TryReadCurrentThreadAffinity(out ulong mask, out int group)
        {
            // Read only. There is no setter for this here, and a thread's own affinity is never changed: it is
            // consulted because Windows respects a restrictive mask above any CPU Set assignment.
            if (!GetThreadGroupAffinity(GetCurrentThread(), out GroupAffinity affinity))
            {
                mask = 0;
                group = -1;
                return false;
            }

            mask = (ulong)affinity.mask;
            group = affinity.group;
            return mask != 0;
        }

        public bool TryReadSelectedCpuSets(List<uint> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            IntPtr thread = GetCurrentThread();

            // With no buffer this answers how many IDs there are. No assignment of its own is a success reporting
            // zero, which is exactly what has to be told apart from a failure to read.
            bool probed = GetThreadSelectedCpuSets(thread, null, 0, out uint required);
            if (required == 0)
            {
                return probed;
            }

            if (required > MaximumCpuSetIds)
            {
                return false;
            }

            var ids = new uint[required];
            if (!GetThreadSelectedCpuSets(thread, ids, required, out uint filled) || filled > required)
            {
                return false;
            }

            for (int i = 0; i < filled; i++)
            {
                into.Add(ids[i]);
            }

            return true;
        }

        public bool TrySetSelectedCpuSets(IReadOnlyList<uint> ids)
        {
            IntPtr thread = GetCurrentThread();
            if (ids == null || ids.Count == 0)
            {
                // Clears the assignment, rather than naming every CPU Set, which is a different thing.
                return SetThreadSelectedCpuSets(thread, null, 0);
            }

            if (ids.Count > MaximumCpuSetIds)
            {
                return false;
            }

            var array = new uint[ids.Count];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = ids[i];
            }

            return SetThreadSelectedCpuSets(thread, array, (uint)array.Length);
        }

        // ----- the whole Win32 surface ---------------------------------------------------------------------------

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(
            IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadSelectedCpuSets(
            IntPtr thread, [Out] uint[] cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[] cpuSetIds, uint cpuSetIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(
            IntPtr process, out UIntPtr processAffinityMask, out UIntPtr systemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity groupAffinity);

        /// <summary>The documented <c>GROUP_AFFINITY</c>: a group-relative mask, its group, and reserved words.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public UIntPtr mask;
            public ushort group;
            public ushort reserved0;
            public ushort reserved1;
            public ushort reserved2;
        }
    }
}
