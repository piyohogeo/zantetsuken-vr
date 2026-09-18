namespace Zantetsu.Rendering
{
    /// <summary>
    /// One geometry of a <see cref="VpCpuGeometryStorage"/>: the handle of its published index range, where the storage
    /// keeps the metadata that belongs to the same append — the vertex blocks the geometry is made of, the render
    /// vertex to topology vertex mapping that goes with them, and the submesh descriptors — and the vertex range this
    /// particular append added. The index start is not copied, since the range can be retired and its space reused;
    /// query it through the handle.
    /// <para>
    /// The geometry's vertices are the ordered union of its blocks, [<see cref="blockStart"/>, blockStart +
    /// <see cref="blockCount"/>) of the storage's block array, and not one contiguous run: a geometry a cut produced
    /// names its parent's blocks and the block of vertices the cut appended, which it shares with the other side.
    /// <see cref="vertexStart"/> and <see cref="vertexCount"/> describe only what this append itself added — the whole
    /// geometry for a mesh or a prepared geometry, the shared appended vertices for a cut result, and an empty range
    /// when a cut added none.
    /// </para>
    /// <para>
    /// <see cref="hasTopology"/> records whether the geometry has a topology mapping at all, which an empty geometry
    /// could not otherwise be told from one whose mapping is absent: <see cref="topologyVertexCount"/> is 0 in both
    /// cases. Topology ids are dense and geometry-local, [0, topologyVertexCount); the storage neither welds by
    /// position nor renumbers them into a global space. A child's id space extends its parent's, so the ids of an
    /// inherited block keep their meaning.
    /// </para>
    /// </summary>
    public readonly struct VpStoredGeometry
    {
        /// <summary>The vertices this append added; for a cut result the ones both sides share.</summary>
        public readonly int vertexStart;

        public readonly int vertexCount;
        public readonly VpIndexRangeHandle indexRange;

        /// <summary>Whether this geometry has a render vertex to topology vertex mapping.</summary>
        public readonly bool hasTopology;

        /// <summary>The number of topology vertices the mapping addresses; 0 without a mapping.</summary>
        public readonly int topologyVertexCount;

        /// <summary>The geometry's first submesh descriptor in the storage.</summary>
        public readonly int submeshStart;

        public readonly int submeshCount;

        /// <summary>The geometry's first vertex block in the storage.</summary>
        public readonly int blockStart;

        public readonly int blockCount;

        /// <summary>
        /// Whether this geometry was accepted as a cut input: appended through
        /// <see cref="VpCpuGeometryStorage.TryAppendCuttable"/> after passing <see cref="VpCutInputGate"/>, or produced
        /// by a cut of a geometry that was. Having a topology mapping is not enough — an ordinary prepared append keeps
        /// its mapping and stays displayable, but is not cuttable. Only the storage sets this, and it compares it
        /// against its own record of the append, so a copy with the flag changed is not one of its geometries.
        /// </summary>
        public readonly bool cutInputAccepted;

        internal VpStoredGeometry(
            int vertexStart,
            int vertexCount,
            VpIndexRangeHandle indexRange,
            bool hasTopology,
            int topologyVertexCount,
            int submeshStart,
            int submeshCount,
            int blockStart,
            int blockCount)
            : this(vertexStart, vertexCount, indexRange, hasTopology, topologyVertexCount, submeshStart, submeshCount, blockStart, blockCount, false)
        {
        }

        internal VpStoredGeometry(
            int vertexStart,
            int vertexCount,
            VpIndexRangeHandle indexRange,
            bool hasTopology,
            int topologyVertexCount,
            int submeshStart,
            int submeshCount,
            int blockStart,
            int blockCount,
            bool cutInputAccepted)
        {
            this.cutInputAccepted = cutInputAccepted;
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
            this.indexRange = indexRange;
            this.hasTopology = hasTopology;
            this.topologyVertexCount = topologyVertexCount;
            this.submeshStart = submeshStart;
            this.submeshCount = submeshCount;
            this.blockStart = blockStart;
            this.blockCount = blockCount;
        }
    }
}
