using System.Diagnostics;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The number of topology connected components of a geometry's triangles (SPLIT-CANDIDATES-3, 2026-11-13): what the
    /// additional split asks before it issues an attempt -- a geometry of one component cannot be shared out between two
    /// children that each hold a triangle, since the split cuts no triangle.
    /// <para>
    /// **The definition.** The triangles are the ones the geometry's own indices name, and nothing else: a vertex an
    /// ancestor left behind and no index names is not counted. Two triangles are connected when they share a topology
    /// vertex -- the id the storage keeps for every render vertex (<c>VpCpuGeometryStorage.TopologyOfVertex</c>), so render
    /// vertices split at a material or normal seam connect what they belong to, vertices of every vertex block of the
    /// geometry, cut vertices and cap auxiliary vertices are counted through the same map, and two vertices at one place
    /// with different ids are two. The count is exact: 0 for no triangle, 1, or more.
    /// </para>
    /// <para>
    /// **Unknown is not a count.** <see cref="Unknown"/> (-1) says the number was not taken; a reader that finds it goes on
    /// as if this class did not exist.
    /// </para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class VpTopologyComponents
    {
        /// <summary>The count was not taken (or could not be: an index or an id out of range).</summary>
        public const int Unknown = -1;

        /// <summary>
        /// Whether the counts are taken where geometries are made (the cut input gate, a cut's two sides, a split's two shares)
        /// and kept by the storage. Off: nothing is counted and every geometry's count is unknown. On by default since
        /// 2026-11-13; a launch sets it before any model or geometry is made (the additional split's launch settings).
        /// </summary>
        public static bool Enabled = true;

        private static long s_counts, s_ticks, s_indices, s_scratchPeak;

        /// <summary>Counts taken through <see cref="CountWithOwnScratch"/>, their time, the indices they read and the largest scratch one took (bytes).</summary>
        public static long Counts => Interlocked.Read(ref s_counts);
        public static double Seconds => Interlocked.Read(ref s_ticks) / (double)Stopwatch.Frequency;
        public static long IndicesRead => Interlocked.Read(ref s_indices);
        public static long ScratchPeakBytes => Interlocked.Read(ref s_scratchPeak);

        public static void ResetTallies()
        {
            Interlocked.Exchange(ref s_counts, 0);
            Interlocked.Exchange(ref s_ticks, 0);
            Interlocked.Exchange(ref s_indices, 0);
            Interlocked.Exchange(ref s_scratchPeak, 0);
        }

        /// <summary>
        /// The components of <paramref name="indexCount"/> indices (whole triangles) over the storage's topology map.
        /// <paramref name="topologyOfVertex"/> is addressed by the indices themselves (global vertex numbers);
        /// <paramref name="parent"/> holds <paramref name="topologyVertexCount"/> entries, every one -1 on entry; what the
        /// indices touched is left written (the caller clears it whole before another run: one fill of the scratch costs far
        /// less than a second walk over the indices). <see cref="Unknown"/> when an index is at or past
        /// <paramref name="vertexLimit"/> or an id is out of range.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int Count(uint* indices, int indexCount, int* topologyOfVertex, uint vertexLimit, int* parent, int topologyVertexCount)
        {
            int distinct = 0, unions = 0;
            bool valid = (indexCount % 3) == 0;
            int read = 0;
            for (; valid && read + 2 < indexCount; read += 3)
            {
                uint ia = indices[read], ib = indices[read + 1], ic = indices[read + 2];
                if (ia >= vertexLimit || ib >= vertexLimit || ic >= vertexLimit) { valid = false; break; }
                int a = topologyOfVertex[ia], b = topologyOfVertex[ib], c = topologyOfVertex[ic];
                if ((uint)a >= (uint)topologyVertexCount || (uint)b >= (uint)topologyVertexCount || (uint)c >= (uint)topologyVertexCount) { valid = false; break; }
                if (parent[a] < 0) { parent[a] = a; distinct++; }
                if (parent[b] < 0) { parent[b] = b; distinct++; }
                if (parent[c] < 0) { parent[c] = c; distinct++; }
                int ra = Find(parent, a), rb = Find(parent, b);
                if (ra != rb) { parent[rb] = ra; unions++; }
                int rc = Find(parent, c);
                if (ra != rc) { parent[rc] = ra; unions++; }
            }

            return valid ? distinct - unions : Unknown;
        }

        private static int Find(int* parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];   // path halving
                x = parent[x];
            }

            return x;
        }

        /// <summary>
        /// The same for two runs of indices over the same map -- a cut's positive and negative sides, a split's two shares --
        /// with one scratch of its own, taken and given back here. Any thread. <see cref="Unknown"/> for both when the scratch
        /// cannot be taken; a run of no indices is 0.
        /// </summary>
        public static void CountWithOwnScratch(
            uint* first, int firstCount, uint* second, int secondCount, int* topologyOfVertex, uint vertexLimit, int topologyVertexCount,
            out int firstComponents, out int secondComponents)
        {
            firstComponents = Unknown;
            secondComponents = Unknown;
            if (topologyOfVertex == null || topologyVertexCount <= 0 || firstCount < 0 || secondCount < 0 || (firstCount > 0 && first == null) || (secondCount > 0 && second == null))
            {
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            long bytes = (long)topologyVertexCount * sizeof(int);
            int* parent = (int*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            if (parent == null) return;
            try
            {
                UnsafeUtility.MemSet(parent, 0xFF, bytes);
                firstComponents = firstCount == 0 ? 0 : Count(first, firstCount, topologyOfVertex, vertexLimit, parent, topologyVertexCount);
                if (firstCount != 0 && secondCount != 0) UnsafeUtility.MemSet(parent, 0xFF, bytes);
                secondComponents = secondCount == 0 ? 0 : Count(second, secondCount, topologyOfVertex, vertexLimit, parent, topologyVertexCount);
            }
            finally
            {
                UnsafeUtility.Free(parent, Allocator.Persistent);
            }

            Interlocked.Increment(ref s_counts);
            Interlocked.Add(ref s_ticks, Stopwatch.GetTimestamp() - begin);
            Interlocked.Add(ref s_indices, (long)firstCount + secondCount);
            long peak;
            while (bytes > (peak = Interlocked.Read(ref s_scratchPeak)) && Interlocked.CompareExchange(ref s_scratchPeak, bytes, peak) != peak) { }
        }

        /// <summary>
        /// The components of triangles given in a mesh's own vertex numbers with the topology id of each vertex: the cut input
        /// gate's form (managed arrays, the main thread). <see cref="Unknown"/> for arrays that do not fit each other.
        /// </summary>
        public static int CountLocal(uint[] localIndices, int[] topologyOfVertex, int topologyVertexCount)
        {
            if (localIndices == null || topologyOfVertex == null || topologyVertexCount < 0 || localIndices.Length % 3 != 0) return Unknown;
            if (localIndices.Length == 0) return 0;
            if (topologyVertexCount == 0) return Unknown;
            fixed (uint* indices = localIndices)
            fixed (int* topology = topologyOfVertex)
            {
                CountWithOwnScratch(indices, localIndices.Length, null, 0, topology, (uint)topologyOfVertex.Length, topologyVertexCount, out int components, out _);
                return components;
            }
        }
    }
}
