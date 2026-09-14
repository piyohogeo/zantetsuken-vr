namespace Zantetsu.Rendering
{
    /// <summary>
    /// One mesh appended to a <see cref="VpCpuGeometryStorage"/>: its committed vertex range and the handle of its
    /// published index range. The index start is not copied, since the range can be retired and its space reused;
    /// query it through the handle.
    /// </summary>
    public readonly struct VpStoredGeometry
    {
        public readonly int vertexStart;
        public readonly int vertexCount;
        public readonly VpIndexRangeHandle indexRange;

        internal VpStoredGeometry(int vertexStart, int vertexCount, VpIndexRangeHandle indexRange)
        {
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
            this.indexRange = indexRange;
        }
    }
}
