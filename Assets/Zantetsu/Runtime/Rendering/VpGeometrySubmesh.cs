namespace Zantetsu.Rendering
{
    /// <summary>
    /// One submesh of a geometry stored in a <see cref="VpCpuGeometryStorage"/>: the part of that geometry's published
    /// index range it covers, and the source material it was assigned. Blittable and self-contained.
    /// <para>
    /// <see cref="indexOffset"/> is relative to the start of the geometry's published index range, not a position in
    /// the physical index buffer, so the range can be retired and its space reused without invalidating the descriptor.
    /// <see cref="materialIndex"/> is the non-negative material index of the source asset; no material name, Unity
    /// Material or texture is kept here, and binding one is the display side's business.
    /// </para>
    /// </summary>
    public readonly struct VpGeometrySubmesh
    {
        public readonly int indexOffset;
        public readonly int indexCount;
        public readonly int materialIndex;

        public VpGeometrySubmesh(int indexOffset, int indexCount, int materialIndex)
        {
            this.indexOffset = indexOffset;
            this.indexCount = indexCount;
            this.materialIndex = materialIndex;
        }
    }
}
