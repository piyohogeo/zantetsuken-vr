using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>The coordinate basis a <see cref="PreparedCommonGeometry"/> is expressed in.</summary>
    public enum CommonGeometryBasis
    {
        /// <summary>The FBX file's own coordinates as <see cref="FbxGeometryImport"/> reads them (metres, no axis change).</summary>
        FbxFile = 0,
        /// <summary>Unity's coordinates: the fixed change of basis of <see cref="FbxUnityBasisChange"/> applied once.</summary>
        Unity = 1,
    }

    /// <summary>One submesh of a prepared geometry: a contiguous index range and the FBX material it was assigned.</summary>
    public struct CommonGeometrySubmesh
    {
        public int IndexStart;
        public int IndexCount;
        /// <summary>The file's material index this submesh holds, exactly as the import recorded it (kept even when the file lists no material names).</summary>
        public int MaterialIndex;
        /// <summary>Material name from the FBX model's material list; empty when the file lists no materials. No Unity Material or texture is bound here.</summary>
        public string MaterialName;
    }

    /// <summary>
    /// Managed common geometry of one FBX object before any pool placement: render vertices in the kernel's layout,
    /// triangle indices grouped by submesh, and for every render vertex the FBX control point it belongs to (the
    /// logical topology the cut kernel keys on, DESIGN 6.2). Attribute seams are separate render vertices of one
    /// control point and are kept as the file defines them. The instance owns its arrays; nothing here references an
    /// importer, a Unity asset, an allocator or a GPU resource.
    /// </summary>
    public sealed class PreparedCommonGeometry
    {
        public string Name;
        public CommonGeometryBasis Basis;
        public RenderVertex[] Vertices;
        public uint[] Indices;
        /// <summary>Per render vertex: the FBX control point (topology vertex) it belongs to.</summary>
        public int[] TopologyOfVertex;
        public int TopologyVertexCount;
        public CommonGeometrySubmesh[] Submeshes;

        public int TriangleCount => Indices.Length / 3;

        /// <summary>Structural problems (undefined basis, index or topology id out of range, ragged submesh ranges, negative material index); empty when consistent.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (!Enum.IsDefined(typeof(CommonGeometryBasis), Basis)) problems.Add("basis " + (int)Basis + " is not a defined CommonGeometryBasis");
            if (Vertices == null || Indices == null || TopologyOfVertex == null || Submeshes == null) { problems.Add("array missing"); return problems; }
            if (TopologyOfVertex.Length != Vertices.Length) problems.Add("topology map length " + TopologyOfVertex.Length + " != vertices " + Vertices.Length);
            if (Indices.Length % 3 != 0) problems.Add("index count " + Indices.Length + " is not a multiple of 3");
            for (int i = 0; i < TopologyOfVertex.Length; i++)
                if (TopologyOfVertex[i] < 0 || TopologyOfVertex[i] >= TopologyVertexCount) { problems.Add("render vertex " + i + " has topology id " + TopologyOfVertex[i] + " outside [0, " + TopologyVertexCount + ")"); break; }
            for (int i = 0; i < Indices.Length; i++)
                if (Indices[i] >= (uint)Vertices.Length) { problems.Add("index " + i + " references vertex " + Indices[i] + " outside the " + Vertices.Length + " render vertices"); break; }
            int expectedStart = 0;
            for (int s = 0; s < Submeshes.Length; s++)
            {
                var sm = Submeshes[s];
                if (sm.IndexStart != expectedStart) problems.Add("submesh " + s + " starts at " + sm.IndexStart + ", expected " + expectedStart);
                if (sm.IndexCount <= 0 || sm.IndexCount % 3 != 0) problems.Add("submesh " + s + " has index count " + sm.IndexCount);
                if (sm.MaterialIndex < 0) problems.Add("submesh " + s + " has material index " + sm.MaterialIndex);
                expectedStart = sm.IndexStart + sm.IndexCount;
            }
            if (expectedStart != Indices.Length) problems.Add("submesh ranges cover " + expectedStart + " of " + Indices.Length + " indices");
            return problems;
        }
    }

    /// <summary>
    /// The fixed change of basis between the FBX file's coordinates and Unity's, and back. It is one reflection (X is
    /// negated), so it is its own inverse: applying it twice returns the original data and plane. Because a reflection
    /// has determinant -1, every triangle's corner order is reversed so the surface keeps facing the same way; normals
    /// and tangent directions are reflected with the positions; the tangent handedness sign flips because the
    /// bitangent cross(normal, tangent) picks up the determinant. UVs, topology ids, render-vertex identity (seams),
    /// index-to-submesh assignment, submesh order and material mapping are unchanged. Nothing about the input decides
    /// the direction of the change: no signed volume, bounds or match rate is consulted, no vertex is welded and no
    /// attribute is recomputed. An input with an undefined basis or a structural problem is rejected before any output
    /// is allocated; the input is never modified; a new instance owning new arrays is returned.
    /// </summary>
    public static class FbxUnityBasisChange
    {
        static readonly float3 k_reflect = new float3(-1f, 1f, 1f);

        public static float3 Point(float3 p) => p * k_reflect;
        public static float3 Direction(float3 d) => d * k_reflect;
        public static float4 Tangent(float4 t) => new float4(t.xyz * k_reflect, -t.w);
        /// <summary>A plane n.p + w = 0 keeps w: the reflection is orthogonal, so (Rn).(Rp) = n.p.</summary>
        public static float4 Plane(float4 plane) => new float4(plane.xyz * k_reflect, plane.w);

        /// <summary>The other of the two defined bases; an undefined value is rejected rather than mapped.</summary>
        public static CommonGeometryBasis Other(CommonGeometryBasis basis)
        {
            switch (basis)
            {
                case CommonGeometryBasis.FbxFile: return CommonGeometryBasis.Unity;
                case CommonGeometryBasis.Unity: return CommonGeometryBasis.FbxFile;
                default: throw new ArgumentOutOfRangeException(nameof(basis), "basis " + (int)basis + " is not a defined CommonGeometryBasis");
            }
        }

        public static PreparedCommonGeometry Apply(PreparedCommonGeometry source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var problems = source.Validate();
            if (problems.Count > 0) throw new ArgumentException("geometry rejected: " + string.Join("; ", problems), nameof(source));
            CommonGeometryBasis basis = Other(source.Basis);

            var vertices = new RenderVertex[source.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                RenderVertex v = source.Vertices[i];
                vertices[i] = new RenderVertex { position = Point(v.position), normal = Direction(v.normal), uv0 = v.uv0, tangent = Tangent(v.tangent) };
            }
            var indices = new uint[source.Indices.Length];
            for (int t = 0; t < indices.Length; t += 3)
            {
                indices[t] = source.Indices[t];
                indices[t + 1] = source.Indices[t + 2];
                indices[t + 2] = source.Indices[t + 1];
            }
            var submeshes = new CommonGeometrySubmesh[source.Submeshes.Length];
            Array.Copy(source.Submeshes, submeshes, submeshes.Length);
            return new PreparedCommonGeometry
            {
                Name = source.Name,
                Basis = basis,
                Vertices = vertices,
                Indices = indices,
                TopologyOfVertex = (int[])source.TopologyOfVertex.Clone(),
                TopologyVertexCount = source.TopologyVertexCount,
                Submeshes = submeshes,
            };
        }
    }

    /// <summary>
    /// Input preparation of the common geometry from the FBX intake (DESIGN 10.2.3, Phase 0.21). <see cref="FromImported"/>
    /// copies what <see cref="FbxGeometryImport"/> read, in the file's own basis and without any change;
    /// <see cref="PrepareUnityBasis"/> is the explicit step that also applies <see cref="FbxUnityBasisChange"/>.
    /// The raw import itself and its reference runs keep their behaviour. Nothing is guessed: the material of every
    /// submesh must be the one the import recorded, and an import whose record is missing, ragged or out of range is
    /// rejected before any output is allocated.
    /// </summary>
    public static class CommonGeometryPreparation
    {
        public static PreparedCommonGeometry FromImported(ImportedGeometry g)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (!g.Usable) throw new ArgumentException("imported geometry '" + g.GeometryName + "' has no triangles", nameof(g));
            var mesh = g.Mesh;
            int submeshCount = mesh.SubmeshIndexCounts.Count;
            int[] materialOfSubmesh = g.SubmeshMaterialIndex;
            if (materialOfSubmesh == null || materialOfSubmesh.Length != submeshCount)
                throw new ArgumentException("imported geometry '" + g.GeometryName + "' records " + (materialOfSubmesh == null ? "no" : materialOfSubmesh.Length.ToString()) + " submesh material index(es) for " + submeshCount + " submesh(es)", nameof(g));
            string[] materialNames = g.MaterialNames ?? Array.Empty<string>();
            for (int s = 0; s < submeshCount; s++)
            {
                int material = materialOfSubmesh[s];
                if (material < 0) throw new ArgumentException("submesh " + s + " records material index " + material, nameof(g));
                // with a material list every recorded index must name one of its entries; without a list the index is kept and the name stays empty
                if (materialNames.Length > 0 && material >= materialNames.Length)
                    throw new ArgumentException("submesh " + s + " records material index " + material + " but the model lists " + materialNames.Length + " material(s)", nameof(g));
            }

            var submeshes = new CommonGeometrySubmesh[submeshCount];
            int start = 0;
            for (int s = 0; s < submeshCount; s++)
            {
                int material = materialOfSubmesh[s];
                submeshes[s] = new CommonGeometrySubmesh
                {
                    IndexStart = start, IndexCount = mesh.SubmeshIndexCounts[s], MaterialIndex = material,
                    MaterialName = materialNames.Length > 0 ? (materialNames[material] ?? "") : "",
                };
                start += mesh.SubmeshIndexCounts[s];
            }
            return new PreparedCommonGeometry
            {
                Name = g.ModelName,
                Basis = CommonGeometryBasis.FbxFile,
                Vertices = (RenderVertex[])mesh.Vertices.Clone(),
                Indices = (uint[])mesh.Indices.Clone(),
                TopologyOfVertex = (int[])mesh.TopologyOfVertex.Clone(),
                TopologyVertexCount = mesh.TopologyVertexCount,
                Submeshes = submeshes,
            };
        }

        /// <summary>The imported geometry expressed in Unity's basis: <see cref="FromImported"/> followed by one <see cref="FbxUnityBasisChange.Apply"/>.</summary>
        public static PreparedCommonGeometry PrepareUnityBasis(ImportedGeometry g) => FbxUnityBasisChange.Apply(FromImported(g));
    }
}
