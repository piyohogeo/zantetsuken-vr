using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>
    /// Hands a prepared common geometry to a CPU VP geometry storage (DESIGN 4.5.3 / 10.2.3). It is only an adapter:
    /// it converts the Editor-side submesh descriptors into the runtime ones and forwards the arrays, so the runtime
    /// side keeps no reference to any Editor type and the storage decides what it accepts. Only geometry already in
    /// Unity's basis is accepted; the FBX basis is never converted here on the caller's behalf, because the basis change
    /// is an explicit step of the intake (<see cref="FbxUnityBasisChange"/>). Material names stay out of the storage:
    /// the runtime descriptor carries the source material index alone, and binding a Material or a texture is the
    /// display side's business.
    /// </summary>
    public static class CommonGeometryStorage
    {
        /// <summary>
        /// Appends the prepared geometry to the storage. Returns false with a default result, leaving the storage
        /// unchanged, when the geometry is null, is not in <see cref="CommonGeometryBasis.Unity"/>, has any structural
        /// problem of its own (<see cref="PreparedCommonGeometry.Validate"/>), or the storage refuses it.
        /// </summary>
        public static bool TryAppend(VpCpuGeometryStorage storage, PreparedCommonGeometry geometry, out VpStoredGeometry stored)
        {
            stored = default;
            if (storage == null
                || geometry == null
                || geometry.Basis != CommonGeometryBasis.Unity
                || geometry.Validate().Count > 0)
            {
                return false;
            }

            var submeshes = new VpGeometrySubmesh[geometry.Submeshes.Length];
            for (int s = 0; s < submeshes.Length; s++)
            {
                CommonGeometrySubmesh submesh = geometry.Submeshes[s];
                submeshes[s] = new VpGeometrySubmesh(submesh.IndexStart, submesh.IndexCount, submesh.MaterialIndex);
            }

            return storage.TryAppendPrepared(
                geometry.Vertices,
                geometry.Indices,
                geometry.TopologyOfVertex,
                geometry.TopologyVertexCount,
                submeshes,
                out stored);
        }
    }
}
