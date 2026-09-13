using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Bump allocator over a region of the caller's scratch block. An allocation that does not fit sets
    /// <see cref="overflow"/> and returns the region base instead of null, so the caller keeps writing inside the
    /// reserved block (never outside it) and the run ends as CapacityScratch once the flag is observed. The peak is
    /// reported so a caller can reserve exactly on the next attempt.
    /// </summary>
    public unsafe struct ScratchArena
    {
        public byte* basePtr;
        public int capacity;
        public int used;
        public int peak;
        public byte overflow;

        public ScratchArena(byte* basePtr, int capacity)
        {
            this.basePtr = basePtr; this.capacity = capacity; used = 0; peak = 0; overflow = 0;
        }

        public int Remaining => capacity - used;

        public T* Take<T>(int count) where T : unmanaged
        {
            int start = (used + 15) / 16 * 16;
            long bytes = (long)math.max(1, count) * sizeof(T);
            if (basePtr == null || start + bytes > capacity)
            {
                overflow = 1;
                return (T*)basePtr;
            }
            used = start + (int)bytes;
            if (used > peak) peak = used;
            return (T*)(basePtr + start);
        }

        /// <summary>Releases everything taken since construction (the peak and the overflow flag are kept).</summary>
        public void Reset() { used = 0; }
    }

    /// <summary>Open-addressing set of 64-bit keys (empty slot = long.MinValue). Capacity is a power of two at least twice the expected count.</summary>
    public unsafe struct LongSet
    {
        public long* keys;
        public int mask;
        public int count;

        public static LongSet Create(ref ScratchArena arena, int expected)
        {
            int cap = 16;
            while (cap < 2 * expected + 8) cap <<= 1;
            var s = new LongSet { keys = arena.Take<long>(cap), mask = cap - 1, count = 0 };
            if (arena.overflow == 0) for (int i = 0; i < cap; i++) s.keys[i] = long.MinValue;
            return s;
        }

        public bool IsCreated => keys != null;

        static ulong Mix(ulong h, ulong v)
        {
            h ^= v + 0x9E3779B97F4A7C15ul + (h << 6) + (h >> 2);
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9ul;
            h ^= h >> 27; h *= 0x94D049BB133111EBul;
            h ^= h >> 31;
            return h;
        }

        public static int Hash(long key) => (int)(Mix(0x9E3779B9ul, (ulong)key) & 0x7FFFFFFF);

        /// <summary>Adds the key; false when it was already present. Never called past half occupancy by construction.</summary>
        public bool Add(long key)
        {
            int slot = Hash(key) & mask;
            for (int probes = 0; probes <= mask; probes++)
            {
                long k = keys[slot];
                if (k == long.MinValue) { keys[slot] = key; count++; return true; }
                if (k == key) return false;
                slot = (slot + 1) & mask;
            }
            return false;
        }

        public bool Contains(long key)
        {
            int slot = Hash(key) & mask;
            for (int probes = 0; probes <= mask; probes++)
            {
                long k = keys[slot];
                if (k == long.MinValue) return false;
                if (k == key) return true;
                slot = (slot + 1) & mask;
            }
            return false;
        }
    }
}
