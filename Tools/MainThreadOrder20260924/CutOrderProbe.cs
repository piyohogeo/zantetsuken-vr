// Diagnostic template only. Installed temporarily in the PhysicsCut assembly, never shipped.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zantetsu.PhysicsCut
{
    public static class CutOrderProbe
    {
        public static bool Enabled;
        public static int Mode { get; private set; }
        public static readonly long[] Ticks = new long[12];
        public static readonly ulong[] Cycles = new ulong[12];
        public static readonly int[] Counts = new int[12];
        public static readonly string[] Names = { "build", "objects+", "objects-", "colliders+", "colliders-",
            "publish", "SetActive+", "SetActive-", "BuildSide+", "BuildSide-", "newRoot+", "newRoot-" };
        public static readonly double EmptyTicks, EmptyCycles;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        private static ulong ReadCycles()
        {
            // GetCurrentThread's pseudo-handle. User + kernel cycles, never converted to elapsed time.
            if (!QueryThreadCycleTime(new IntPtr(-2), out ulong cycles))
                throw new InvalidOperationException("QueryThreadCycleTime failed: " + Marshal.GetLastWin32Error());
            return cycles;
        }

        static CutOrderProbe()
        {
            Enabled = true;
            for (int i = 0; i < 1000; i++) { using (Span(11)) { } }
            EmptyTicks = Ticks[11] / 1000.0;
            EmptyCycles = Cycles[11] / 1000.0;
            Enabled = false;
            Reset(0);
        }

        public static void Reset(int mode)
        {
            if (mode < 0 || mode > 3) throw new ArgumentOutOfRangeException(nameof(mode));
            Mode = mode;
            Array.Clear(Ticks, 0, Ticks.Length);
            Array.Clear(Cycles, 0, Cycles.Length);
            Array.Clear(Counts, 0, Counts.Length);
        }

        public static Timing Span(int index) => new Timing(index);

        public readonly struct Timing : IDisposable
        {
            private readonly int _index;
            private readonly long _ticks;
            private readonly ulong _cycles;
            internal Timing(int index)
            {
                _index = Enabled ? index : -1;
                _cycles = Enabled ? ReadCycles() : 0;
                _ticks = Enabled ? Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (_index < 0) return;
                long ticks = Stopwatch.GetTimestamp();
                ulong cycles = ReadCycles();
                Ticks[_index] += ticks - _ticks;
                Cycles[_index] += cycles - _cycles;
                Counts[_index]++;
            }
        }
    }
}
