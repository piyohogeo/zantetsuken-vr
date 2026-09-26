using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The bone-local convexes of a prepared character as a hit reads them (DESIGN 19.1.7): one immutable copy of the
    /// authored bank, made once at preparation with each convex's box, in one native block. A hit places convex
    /// <c>i</c> by its bone's world transform as it stands, which is the transform the cut poses the same convex with.
    /// <para>
    /// It is a copy because the prepared physics input writes its current-pose points into its own bank for a cut
    /// attempt and does not write the bind points back after a refusal; a hit must read bind points every time.
    /// Nothing here is posed, cooked or published.
    /// </para>
    /// </summary>
    public sealed unsafe class VpCharacterHitShape : IDisposable
    {
        private NativeArray<byte> _block;
        private readonly ConvexBrepRange[] _ranges;
        private readonly float3[] _lo;
        private readonly float3[] _hi;
        private ConvexBrepBank _bank;

        public VpCharacterHitShape(ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexes)
        {
            if (convexes == null || convexes.Count == 0)
            {
                throw new ArgumentException("at least one convex", nameof(convexes));
            }

            int count = convexes.Count;
            _ranges = new ConvexBrepRange[count];
            _lo = new float3[count];
            _hi = new float3[count];
            long vertices = 0, faceOffsets = 0, faceIndices = 0, edges = 0;
            for (int c = 0; c < count; c++)
            {
                ConvexBrepRange r = convexes[c];
                vertices += r.vertexCount;
                faceOffsets += r.faceCount + 1;
                faceIndices += r.faceIndexCount;
                edges += r.edgeCount;
            }

            long verticesAt = 0;
            long faceOffsetsAt = Align16(verticesAt + vertices * sizeof(float3));
            long faceIndicesAt = Align16(faceOffsetsAt + faceOffsets * sizeof(int));
            long faceEdgesAt = Align16(faceIndicesAt + faceIndices * sizeof(int));
            long edgesAt = Align16(faceEdgesAt + faceIndices * sizeof(int));
            long bytes = Align16(edgesAt + edges * sizeof(BrepEdge));
            _block = new NativeArray<byte>(checked((int)bytes), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            byte* at = (byte*)_block.GetUnsafePtr();
            _bank = new ConvexBrepBank
            {
                vertices = (float3*)(at + verticesAt),
                faceOffsets = (int*)(at + faceOffsetsAt),
                faceIndices = (int*)(at + faceIndicesAt),
                faceEdges = (int*)(at + faceEdgesAt),
                edges = (BrepEdge*)(at + edgesAt),
            };

            int v0 = 0, f0 = 0, i0 = 0, e0 = 0;
            for (int c = 0; c < count; c++)
            {
                ConvexBrepRange r = convexes[c];
                float3 lo = new float3(float.PositiveInfinity);
                float3 hi = new float3(float.NegativeInfinity);
                for (int v = 0; v < r.vertexCount; v++)
                {
                    float3 p = bank.vertices[r.vertexBase + v];
                    if (!math.all(math.isfinite(p)))
                    {
                        Dispose();
                        throw new ArgumentException("finite bone-local points required", nameof(bank));
                    }

                    _bank.vertices[v0 + v] = p;
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }

                // Face offsets and loop entries are relative to the convex's own index base and vertex numbering, so
                // they are copied as they are; only where each convex starts in this block changes.
                UnsafeUtility.MemCpy(_bank.faceOffsets + f0, bank.faceOffsets + r.faceBase, (r.faceCount + 1) * sizeof(int));
                UnsafeUtility.MemCpy(_bank.faceIndices + i0, bank.faceIndices + r.faceIndexBase, r.faceIndexCount * sizeof(int));
                UnsafeUtility.MemCpy(_bank.faceEdges + i0, bank.faceEdges + r.faceIndexBase, r.faceIndexCount * sizeof(int));
                UnsafeUtility.MemCpy(_bank.edges + e0, bank.edges + r.edgeBase, r.edgeCount * sizeof(BrepEdge));
                _ranges[c] = new ConvexBrepRange
                {
                    vertexBase = v0, vertexCount = r.vertexCount,
                    faceBase = f0, faceCount = r.faceCount,
                    faceIndexBase = i0, faceIndexCount = r.faceIndexCount,
                    edgeBase = e0, edgeCount = r.edgeCount,
                    maxFaceLoop = r.maxFaceLoop,
                };
                _lo[c] = lo;
                _hi[c] = hi;
                v0 += r.vertexCount;
                f0 += r.faceCount + 1;
                i0 += r.faceIndexCount;
                e0 += r.edgeCount;
            }
        }

        public int ConvexCount => _ranges.Length;

        public bool IsDisposed => !_block.IsCreated;

        public ConvexBrepBank Bank => _bank;

        public ConvexBrepRange Convex(int index) => _ranges[index];

        /// <summary>The box convex <paramref name="index"/> lies in, in its bone's frame; it holds every vertex.</summary>
        public void Bounds(int index, out float3 lo, out float3 hi)
        {
            lo = _lo[index];
            hi = _hi[index];
        }

        public void Dispose()
        {
            if (_block.IsCreated)
            {
                _block.Dispose();
            }

            _bank = default;
        }

        private static long Align16(long value) => (value + 15) & ~15L;
    }
}
