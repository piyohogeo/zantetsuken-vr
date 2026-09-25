namespace Zantetsu.Rendering
{
    /// <summary>Published immutable Direct16 geometry and its producer identity; not ownership of its storage/range.</summary>
    public readonly struct VpDirectSkinOutput
    {
        readonly VpDirectSkinInput producer;
        readonly VpCpuGeometryStorage storage;
        public VpStoredGeometry Geometry { get; }
        internal VpDirectSkinOutput(VpDirectSkinInput producer, VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        { this.producer = producer; this.storage = storage; Geometry = geometry; }
        public bool IsFrom(VpDirectSkinInput expectedProducer, VpCpuGeometryStorage expectedStorage)
            => producer != null && ReferenceEquals(producer, expectedProducer) && ReferenceEquals(storage, expectedStorage);
    }
}
