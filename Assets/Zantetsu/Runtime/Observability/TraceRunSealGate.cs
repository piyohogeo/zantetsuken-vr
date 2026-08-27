using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Atomic seal gate shared between a capture run <see cref="TraceLogger"/>
    /// and its <see cref="SealableTraceWriter"/> producers. The gate is a
    /// single fixed-length <see cref="NativeArray{T}"/> of <c>int</c> whose
    /// slots are read and written with <see cref="Interlocked"/> so that the
    /// main thread and any number of Burst jobs observe one coherent state.
    /// </summary>
    internal static class TraceRunSealGate
    {
        internal const int SlotSealState = 0;
        internal const int SlotActiveWriters = 1;
        internal const int SlotMutableFailures = 2;
        internal const int SlotSealedFailures = 3;
        internal const int SlotPostSealAttempts = 4;
        internal const int SlotCutoffClosed = 5;
        internal const int SlotCount = 6;

        internal static NativeArray<int> Create()
        {
            return new NativeArray<int>(SlotCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        internal static unsafe ref int SlotRef(NativeArray<int> gate, int slot)
        {
            return ref UnsafeUtility.ArrayElementAsRef<int>(gate.GetUnsafePtr(), slot);
        }

        internal static int Read(NativeArray<int> gate, int slot)
        {
            return Interlocked.CompareExchange(ref SlotRef(gate, slot), 0, 0);
        }

        internal static int Increment(NativeArray<int> gate, int slot)
        {
            return Interlocked.Increment(ref SlotRef(gate, slot));
        }

        internal static int Decrement(NativeArray<int> gate, int slot)
        {
            return Interlocked.Decrement(ref SlotRef(gate, slot));
        }

        internal static int CompareExchange(NativeArray<int> gate, int slot, int value, int comparand)
        {
            return Interlocked.CompareExchange(ref SlotRef(gate, slot), value, comparand);
        }

        internal static void SaturatingAdd(NativeArray<int> gate, int slot, int value)
        {
            ref int cell = ref SlotRef(gate, slot);
            int oldValue = Interlocked.CompareExchange(ref cell, 0, 0);
            while (true)
            {
                if (oldValue >= int.MaxValue)
                {
                    return;
                }

                long next = (long)oldValue + value;
                int clamped = next > int.MaxValue ? int.MaxValue : (int)next;
                int current = Interlocked.CompareExchange(ref cell, clamped, oldValue);
                if (current == oldValue)
                {
                    return;
                }

                oldValue = current;
            }
        }

        /// <summary>
        /// Accounts one rejection toward either the mutable failure count (a
        /// <see cref="TraceRunSealState.Sealing"/> observation before the
        /// cutoff closes) or the post-seal attempt count (everything else).
        /// Each attempt increments at most one of the two counters.
        /// </summary>
        internal static void RecordRejection(NativeArray<int> gate, int sealState)
        {
            bool cutoffClosed = Read(gate, SlotCutoffClosed) != 0;
            int slot = sealState == (int)TraceRunSealState.Sealing && !cutoffClosed
                ? SlotMutableFailures
                : SlotPostSealAttempts;

            SaturatingAdd(gate, slot, 1);
        }

        /// <summary>
        /// Accounts one queue-write failure observed while the run was still
        /// <see cref="TraceRunSealState.Open"/> into the mutable failure count.
        /// Defensive: the native queue cannot normally fail to write.
        /// </summary>
        internal static void RecordMutableFailure(NativeArray<int> gate)
        {
            SaturatingAdd(gate, SlotMutableFailures, 1);
        }
    }
}
