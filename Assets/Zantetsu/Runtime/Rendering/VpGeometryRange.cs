namespace Zantetsu.Rendering
{
    /// <summary>
    /// The vertex and index ranges one appended geometry occupies in the VP pool. Its indices already address the
    /// pool's global vertex numbers.
    /// </summary>
    public readonly struct VpGeometryRange
    {
        public readonly int vertexStart;
        public readonly int vertexCount;
        public readonly int indexStart;
        public readonly int indexCount;

        public VpGeometryRange(int vertexStart, int vertexCount, int indexStart, int indexCount)
        {
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
            this.indexStart = indexStart;
            this.indexCount = indexCount;
        }
    }
}
