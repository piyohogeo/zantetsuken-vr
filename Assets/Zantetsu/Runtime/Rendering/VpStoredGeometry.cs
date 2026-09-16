namespace Zantetsu.Rendering
{
    /// <summary>
    /// One geometry appended to a <see cref="VpCpuGeometryStorage"/>: its committed vertex range, the handle of its
    /// published index range, and where the storage keeps the metadata that belongs to the same append — the render
    /// vertex to topology vertex mapping and the submesh descriptors. The index start is not copied, since the range
    /// can be retired and its space reused; query it through the handle.
    /// <para>
    /// <see cref="hasTopology"/> records whether the append carried a topology mapping at all, which an empty geometry
    /// could not otherwise be told from one whose mapping is absent: <see cref="topologyVertexCount"/> is 0 in both
    /// cases. Topology ids are dense and geometry-local, [0, topologyVertexCount); the storage neither welds by
    /// position nor renumbers them into a global space.
    /// </para>
    /// </summary>
    public readonly struct VpStoredGeometry
    {
        public readonly int vertexStart;
        public readonly int vertexCount;
        public readonly VpIndexRangeHandle indexRange;

        /// <summary>Whether this append carried a render vertex to topology vertex mapping.</summary>
        public readonly bool hasTopology;

        /// <summary>The number of topology vertices the mapping addresses; 0 without a mapping.</summary>
        public readonly int topologyVertexCount;

        /// <summary>The geometry's first submesh descriptor in the storage.</summary>
        public readonly int submeshStart;

        public readonly int submeshCount;

        internal VpStoredGeometry(
            int vertexStart,
            int vertexCount,
            VpIndexRangeHandle indexRange,
            bool hasTopology,
            int topologyVertexCount,
            int submeshStart,
            int submeshCount)
        {
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
            this.indexRange = indexRange;
            this.hasTopology = hasTopology;
            this.topologyVertexCount = topologyVertexCount;
            this.submeshStart = submeshStart;
            this.submeshCount = submeshCount;
        }
    }
}
