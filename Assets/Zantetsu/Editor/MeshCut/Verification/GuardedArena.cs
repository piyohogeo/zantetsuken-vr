using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>
    /// Test-side arena that places guard bytes after every region and detects overruns after a kernel call. Regions can
    /// be pre-filled with a chosen byte or scrambled to prove the kernel does not depend on zeroed or previously used
    /// memory. Product code never scans for this.
    /// </summary>
    public sealed unsafe class GuardedArena : IDisposable
    {
        public const int GuardBytes = 64;
        const byte k_pattern = 0xA5;

        NativeArray<byte> m_bytes;
        int m_used;
        byte m_fill = k_pattern;
        readonly List<(string name, int offset, int length)> m_regions = new List<(string, int, int)>();

        public int Used => m_used;
        public int Capacity => m_bytes.Length;

        public GuardedArena(int capacityBytes)
        {
            m_bytes = new NativeArray<byte>(capacityBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemSet(m_bytes.GetUnsafePtr(), k_pattern, capacityBytes);
        }

        public void Reset(byte fill = k_pattern)
        {
            m_used = 0;
            m_fill = fill;
            m_regions.Clear();
            UnsafeUtility.MemSet(m_bytes.GetUnsafePtr(), k_pattern, m_bytes.Length);
        }

        public T* Alloc<T>(string name, int count) where T : unmanaged
        {
            int align = Math.Max(16, UnsafeUtility.AlignOf<T>());
            int start = (m_used + align - 1) / align * align;
            int bytes = Math.Max(1, count) * UnsafeUtility.SizeOf<T>();
            if (start + bytes + GuardBytes > m_bytes.Length) throw new InvalidOperationException("GuardedArena capacity exceeded for " + name + " (" + bytes + " bytes)");
            m_regions.Add((name, start, bytes));
            m_used = start + bytes + GuardBytes;
            if (m_fill != k_pattern) UnsafeUtility.MemSet((byte*)m_bytes.GetUnsafePtr() + start, m_fill, bytes);
            return (T*)((byte*)m_bytes.GetUnsafePtr() + start);
        }

        public void Fill(string name, byte value)
        {
            foreach (var (n, offset, length) in m_regions)
                if (n == name) UnsafeUtility.MemSet((byte*)m_bytes.GetUnsafePtr() + offset, value, length);
        }

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

        /// <summary>Names of regions whose guard bytes were overwritten.</summary>
        public List<string> CheckGuards()
        {
            var bad = new List<string>();
            byte* p = (byte*)m_bytes.GetUnsafePtr();
            foreach (var (name, offset, length) in m_regions)
                for (int i = 0; i < GuardBytes; i++)
                    if (p[offset + length + i] != k_pattern) { bad.Add(name); break; }
            return bad;
        }

        public ulong HashRegion(string name)
        {
            ulong h = 14695981039346656037UL;
            byte* p = (byte*)m_bytes.GetUnsafePtr();
            foreach (var (n, offset, length) in m_regions)
            {
                if (n != name) continue;
                for (int i = 0; i < length; i++) { h ^= p[offset + i]; h *= 1099511628211UL; }
            }
            return h;
        }

        public void Dispose() { if (m_bytes.IsCreated) m_bytes.Dispose(); }
    }
}
