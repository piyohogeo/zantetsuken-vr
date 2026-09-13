using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Test-only arena that places guard bytes after every logical region and detects overruns after a kernel call.
    /// Regions can be pre-filled with a chosen byte pattern to prove that the kernels do not depend on zeroed or
    /// previously used scratch. Product code never scans for this.
    /// </summary>
    public sealed unsafe class SentinelArena : IDisposable
    {
        public const int GuardBytes = 64;
        const byte k_pattern = 0xA5;

        NativeArray<byte> m_bytes;
        int m_used;
        byte m_fill = k_pattern;
        readonly List<(string name, int offset, int length)> m_regions = new List<(string, int, int)>();
        public int Used => m_used;
        public int Capacity => m_bytes.Length;

        public SentinelArena(int capacityBytes)
        {
            m_bytes = new NativeArray<byte>(capacityBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemSet(m_bytes.GetUnsafePtr(), k_pattern, capacityBytes);
        }

        /// Resets the arena; regions allocated afterwards are filled with <paramref name="fill"/> (guards keep the guard pattern).
        public void Reset(byte fill = k_pattern)
        {
            m_used = 0;
            m_fill = fill;
            m_regions.Clear();
            UnsafeUtility.MemSet(m_bytes.GetUnsafePtr(), k_pattern, m_bytes.Length);
        }

        /// Reserves `count` elements of T followed by a guard region. Returns a sub-array view of exactly `count`.
        public NativeArray<T> Alloc<T>(string name, int count) where T : unmanaged
        {
            int align = UnsafeUtility.AlignOf<T>();
            int start = (m_used + align - 1) / align * align;
            int bytes = Math.Max(1, count) * UnsafeUtility.SizeOf<T>();
            if (start + bytes + GuardBytes > m_bytes.Length) throw new InvalidOperationException("SentinelArena capacity exceeded for " + name);
            m_regions.Add((name, start, bytes));
            m_used = start + bytes + GuardBytes;
            if (m_fill != k_pattern) UnsafeUtility.MemSet((byte*)m_bytes.GetUnsafePtr() + start, m_fill, bytes);
            var arr = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>((byte*)m_bytes.GetUnsafePtr() + start, Math.Max(1, count), Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref arr, NativeArrayUnsafeUtility.GetAtomicSafetyHandle(m_bytes));
#endif
            return arr;
        }

        public T* AllocPtr<T>(string name, int count) where T : unmanaged => (T*)Alloc<T>(name, count).GetUnsafePtr();

        /// Fills a previously allocated region with pseudo-random bytes (deterministic per seed).
        public void Scramble(string name, uint seed)
        {
            foreach (var (n, offset, length) in m_regions)
            {
                if (n != name) continue;
                var rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
                byte* p = (byte*)m_bytes.GetUnsafePtr() + offset;
                for (int i = 0; i < length; i++) p[i] = (byte)rng.NextUInt(256);
            }
        }

        /// Returns the names of regions whose guard bytes were overwritten.
        public List<string> CheckGuards()
        {
            var bad = new List<string>();
            byte* p = (byte*)m_bytes.GetUnsafePtr();
            foreach (var (name, offset, length) in m_regions)
            {
                for (int i = 0; i < GuardBytes; i++)
                    if (p[offset + length + i] != k_pattern) { bad.Add(name); break; }
            }
            return bad;
        }

        public void Dispose() { if (m_bytes.IsCreated) m_bytes.Dispose(); }
    }
}
