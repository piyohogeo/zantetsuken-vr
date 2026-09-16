namespace Zantetsu.Rendering
{
    /// <summary>
    /// One contiguous block of a geometry's render vertices in a <see cref="VpCpuGeometryStorage"/>, whose topology
    /// vertex ids sit at the same positions of the storage's mapping array. A geometry is the ordered union of its
    /// blocks (DESIGN 6.2), which is what lets a cut child name its parent's vertices and the vertices the cut appended
    /// without copying either: the child records the blocks, not the data.
    /// <para>
    /// Committed vertices and their mapping entries are never moved or overwritten, so a block stays valid for as long
    /// as the storage lives, whatever happens to the index range of the geometry the block came from.
    /// </para>
    /// </summary>
    public readonly struct VpGeometryVertexBlock
    {
        public readonly int vertexStart;
        public readonly int vertexCount;

        public VpGeometryVertexBlock(int vertexStart, int vertexCount)
        {
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
        }
    }
}
