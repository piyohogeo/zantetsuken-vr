using Unity.Collections;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Room a <see cref="VpCpuGeometryStorage"/> has set aside for the output of one cut of <see cref="Parent"/>: the
    /// uncommitted vertex tail and the mapping entries that go with it one for one, an index range reserved for both
    /// sides at once, and the metadata room the two sides will need. The cut writes straight into these views, so no
    /// output is copied into the storage afterwards.
    /// <para>
    /// The views may be used only while the reservation is open, that is until the storage commits or cancels it.
    /// Nothing here is visible to a reader of the storage: the vertices, the mapping entries and the metadata stay
    /// uncommitted and the index range stays Reserved, so abandoning a reservation leaves no trace beyond bytes in
    /// space that is free again. Index space taken here is one contiguous run, which a commit splits into the two
    /// sides' own ranges (DESIGN 4.5.6).
    /// </para>
    /// </summary>
    public sealed class VpCutOutputReservation
    {
        private readonly NativeArray<VpRenderVertex> _newVertices;
        private readonly NativeArray<int> _newVertexTopology;
        private readonly NativeArray<uint> _newIndices;

        internal readonly VpStoredGeometry parent;
        internal readonly VpIndexRangeHandle indexRange;

        // The descriptor the second side's range takes at a two-sided commit: an empty reservation owned from the
        // moment this was reserved, so no registration made while the cut runs can leave the commit without one.
        internal readonly VpIndexRangeHandle splitDescriptor;
        internal readonly int indexStart;
        internal readonly int vertexStart;
        internal readonly int submeshStart;
        internal readonly int vertexBlockStart;
        internal readonly int submeshCapacity;
        internal readonly int vertexBlockCapacity;
        internal bool closed;

        internal VpCutOutputReservation(
            VpStoredGeometry parent,
            VpIndexRangeHandle indexRange,
            VpIndexRangeHandle splitDescriptor,
            int indexStart,
            int vertexStart,
            int submeshStart,
            int vertexBlockStart,
            int submeshCapacity,
            int vertexBlockCapacity,
            NativeArray<VpRenderVertex> newVertices,
            NativeArray<int> newVertexTopology,
            NativeArray<uint> newIndices)
        {
            this.parent = parent;
            this.indexRange = indexRange;
            this.splitDescriptor = splitDescriptor;
            this.indexStart = indexStart;
            this.vertexStart = vertexStart;
            this.submeshStart = submeshStart;
            this.vertexBlockStart = vertexBlockStart;
            this.submeshCapacity = submeshCapacity;
            this.vertexBlockCapacity = vertexBlockCapacity;
            _newVertices = newVertices;
            _newVertexTopology = newVertexTopology;
            _newIndices = newIndices;
        }

        /// <summary>The geometry this cut reads; its vertices and mapping entries are the ones a child inherits.</summary>
        public VpStoredGeometry Parent => parent;

        /// <summary>Whether the reservation has already been committed or cancelled, after which its views are stale.</summary>
        public bool IsClosed => closed;

        /// <summary>Room for the vertices the cut appends, which both sides share. Written from its start.</summary>
        public NativeArray<VpRenderVertex> NewVertices => _newVertices;

        /// <summary>The topology vertex id of each appended vertex, at the same position as the vertex itself.</summary>
        public NativeArray<int> NewVertexTopology => _newVertexTopology;

        /// <summary>The one index reservation, written positive side first and then negative (DESIGN 4.5.6).</summary>
        public NativeArray<uint> NewIndices => _newIndices;

        /// <summary>The global vertex number the first appended vertex will have.</summary>
        public uint NewVertexBase => (uint)vertexStart;

        /// <summary>
        /// Where the reservation sits in the storage's index buffer, which is the base of DESIGN 4.5.6: the positive
        /// side occupies [IndexStart, IndexStart + n0) and the negative side the n1 that follow it.
        /// </summary>
        public int IndexStart => indexStart;

        public int NewVertexCapacity => _newVertices.Length;

        public int NewIndexCapacity => _newIndices.Length;

        /// <summary>The largest number of submesh descriptors the two sides may use together.</summary>
        /// <summary>Where this reservation's vertices begin: its own span, shared with no other.</summary>
        public int VertexStart => vertexStart;

        /// <summary>Where this reservation's submesh descriptors begin: its own span, shared with no other.</summary>
        public int SubmeshStart => submeshStart;

        /// <summary>Where this reservation's vertex blocks begin: its own span, shared with no other.</summary>
        public int VertexBlockStart => vertexBlockStart;

        public int SubmeshCapacity => submeshCapacity;

        /// <summary>The largest number of vertex blocks the children's shared block list may use.</summary>
        public int VertexBlockCapacity => vertexBlockCapacity;
    }
}
