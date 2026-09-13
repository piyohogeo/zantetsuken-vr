using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>One mesh object read from an FBX: the cut kernel's input in local numbering, plus what the intake records about it.</summary>
    public sealed class ImportedGeometry
    {
        public string ModelName, GeometryName;
        public SyntheticMesh Mesh;
        public int ControlPoints, Polygons, NonTrianglePolygons, RenderVertices;
        public string[] MaterialNames = Array.Empty<string>();
        public string NormalMapping, UvMapping, TangentMapping, MaterialMapping;
        public bool HasUv, HasTangent;
        public float3 BoundsMin, BoundsMax;
        public readonly List<string> Notes = new List<string>();
        public bool Usable => Mesh != null && Mesh.TriangleCount > 0;
    }

    public sealed class FbxImportResult
    {
        public uint Version;
        public double UnitScaleFactor = 1.0;
        public readonly List<ImportedGeometry> Geometries = new List<ImportedGeometry>();
        public readonly List<string> TextureFiles = new List<string>();
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Turns an FBX file into cut-kernel inputs the way DESIGN 6.2 requires: the logical topology is the file's own
    /// control-point index (Blender's logical vertex), so UV / normal / material seams are attribute splits of one
    /// topology vertex and never a positional weld; per-corner normals, UVs and tangents are deduplicated per control
    /// point into render vertices exactly as the probe's exporter did; materials become submesh ranges. Positions are
    /// converted to metres from the file's unit scale; model transforms are expected to be identity (the export bakes
    /// them) and are reported as a note otherwise. Nothing is repaired or guessed: a polygon that is not a triangle is
    /// fan-triangulated and counted, and every deviation is recorded for the run.
    /// </summary>
    public static class FbxGeometryImport
    {
        public static FbxImportResult Import(string path)
        {
            var result = new FbxImportResult();
            FbxNode root = FbxBinaryReader.Read(path, out result.Version);
            var settings = root.Child("GlobalSettings")?.Property70("UnitScaleFactor");
            if (settings != null && settings.Properties.Count >= 5) result.UnitScaleFactor = settings.PropertyDouble(4);
            double toMetres = result.UnitScaleFactor / 100.0;

            var objects = root.Child("Objects");
            if (objects == null) { result.Notes.Add("no Objects node"); return result; }

            // connections: child id -> parent ids
            var parents = new Dictionary<long, List<long>>();
            var connections = root.Child("Connections");
            if (connections != null)
                foreach (var c in connections.ChildrenNamed("C"))
                {
                    if (c.Properties.Count < 3 || (c.Properties[0] as string) != "OO") continue;
                    long child = c.PropertyLong(1), parent = c.PropertyLong(2);
                    if (!parents.TryGetValue(child, out var list)) { list = new List<long>(); parents.Add(child, list); }
                    list.Add(parent);
                }

            var models = new Dictionary<long, FbxNode>();
            var materials = new Dictionary<long, string>();
            foreach (var m in objects.ChildrenNamed("Model")) models[m.PropertyLong(0)] = m;
            foreach (var m in objects.ChildrenNamed("Material")) materials[m.PropertyLong(0)] = CleanName(m.PropertyString(1));
            foreach (var t in objects.ChildrenNamed("Texture"))
            {
                var rel = t.Child("RelativeFilename");
                if (rel != null && rel.Properties.Count > 0) result.TextureFiles.Add(rel.PropertyString(0));
            }

            foreach (var geometry in objects.ChildrenNamed("Geometry"))
            {
                if (geometry.Properties.Count < 3 || (geometry.PropertyString(2)) != "Mesh") continue;
                long geometryId = geometry.PropertyLong(0);
                var g = new ImportedGeometry { GeometryName = CleanName(geometry.PropertyString(1)) };
                FbxNode model = null;
                if (parents.TryGetValue(geometryId, out var geometryParents))
                    foreach (long p in geometryParents) if (models.TryGetValue(p, out model)) break;
                g.ModelName = model != null ? CleanName(model.PropertyString(1)) : g.GeometryName;
                if (model != null)
                {
                    foreach (var (name, identity) in new[] { ("Lcl Translation", new double3(0)), ("Lcl Rotation", new double3(0)), ("Lcl Scaling", new double3(1)) })
                    {
                        var p = model.Property70(name);
                        if (p == null || p.Properties.Count < 7) continue;
                        var v = new double3(p.PropertyDouble(4), p.PropertyDouble(5), p.PropertyDouble(6));
                        if (math.any(math.abs(v - identity) > 1e-9)) g.Notes.Add(name + " is not identity (" + v + "); the import does not apply model transforms");
                    }
                    // material order on the model
                    var materialNames = new List<string>();
                    if (parents.TryGetValue(model.PropertyLong(0), out _)) { }
                    foreach (var kv in parents)
                        foreach (long p in kv.Value)
                            if (p == model.PropertyLong(0) && materials.TryGetValue(kv.Key, out string mn)) materialNames.Add(mn);
                    g.MaterialNames = materialNames.ToArray();
                }
                ReadMesh(geometry, g, toMetres);
                result.Geometries.Add(g);
            }
            return result;
        }

        static string CleanName(string s)
        {
            if (s == null) return "";
            int cut = s.IndexOf('\0');
            return cut >= 0 ? s.Substring(0, cut) : s;
        }

        static void ReadMesh(FbxNode geometry, ImportedGeometry g, double toMetres)
        {
            var vertices = geometry.Child("Vertices")?.Property<double[]>(0);
            var polygonIndex = geometry.Child("PolygonVertexIndex")?.Property<int[]>(0);
            if (vertices == null || polygonIndex == null) { g.Notes.Add("geometry has no vertices or polygons"); return; }
            int cp = vertices.Length / 3;
            g.ControlPoints = cp;
            var positions = new float3[cp];
            g.BoundsMin = new float3(float.MaxValue); g.BoundsMax = new float3(float.MinValue);
            for (int i = 0; i < cp; i++)
            {
                positions[i] = new float3((float)(vertices[3 * i] * toMetres), (float)(vertices[3 * i + 1] * toMetres), (float)(vertices[3 * i + 2] * toMetres));
                g.BoundsMin = math.min(g.BoundsMin, positions[i]); g.BoundsMax = math.max(g.BoundsMax, positions[i]);
            }

            // polygons: corner list per polygon (negative index terminates a polygon: value = -(index + 1))
            var polygons = new List<int[]>();
            var current = new List<int>();
            var cornerOfLoop = new List<int>();   // loop (polygon-vertex) index per corner, in file order
            for (int i = 0; i < polygonIndex.Length; i++)
            {
                int v = polygonIndex[i];
                bool last = v < 0;
                current.Add(last ? -v - 1 : v);
                if (last) { polygons.Add(current.ToArray()); current = new List<int>(); }
            }
            g.Polygons = polygons.Count;

            // layers (per polygon vertex, direct or index-to-direct)
            double[] normals = ReadLayer(geometry, "LayerElementNormal", "Normals", "NormalsIndex", out g.NormalMapping, polygonIndex.Length, 3);
            double[] uvs = ReadLayer(geometry, "LayerElementUV", "UV", "UVIndex", out g.UvMapping, polygonIndex.Length, 2);
            double[] tangents = ReadLayer(geometry, "LayerElementTangent", "Tangents", "TangentsIndex", out g.TangentMapping, polygonIndex.Length, 3);
            double[] binormals = ReadLayer(geometry, "LayerElementBinormal", "Binormals", "BinormalsIndex", out _, polygonIndex.Length, 3);
            g.HasUv = uvs != null; g.HasTangent = tangents != null;
            if (normals == null) g.Notes.Add("no per-corner normals; smooth normals are not synthesized (the kernel input needs the file's own)");

            int[] polygonMaterial = null;
            var layerMaterial = geometry.Child("LayerElementMaterial");
            if (layerMaterial != null)
            {
                g.MaterialMapping = layerMaterial.Child("MappingInformationType")?.PropertyString(0);
                var mats = layerMaterial.Child("Materials")?.Property<int[]>(0);
                if (mats != null)
                {
                    polygonMaterial = new int[polygons.Count];
                    for (int p = 0; p < polygons.Count; p++) polygonMaterial[p] = g.MaterialMapping == "AllSame" ? (mats.Length > 0 ? mats[0] : 0) : (p < mats.Length ? mats[p] : 0);
                }
            }

            // corners -> render vertices, deduplicated per control point by exact attribute equality
            var renderOfKey = new Dictionary<(int, float3, float2, float4), uint>();
            var renderVertices = new List<RenderVertex>();
            var renderTopology = new List<int>();
            var triangles = new List<(uint a, uint b, uint c, int material)>();
            int loop = 0;
            int nonTri = 0;
            for (int p = 0; p < polygons.Count; p++)
            {
                int[] poly = polygons[p];
                var corner = new uint[poly.Length];
                for (int k = 0; k < poly.Length; k++, loop++)
                {
                    int v = poly[k];
                    float3 n = normals != null ? Normalize(new float3((float)normals[3 * loop], (float)normals[3 * loop + 1], (float)normals[3 * loop + 2]), new float3(0, 1, 0)) : new float3(0, 1, 0);
                    float2 uv = uvs != null ? new float2((float)uvs[2 * loop], (float)uvs[2 * loop + 1]) : float2.zero;
                    float4 t;
                    if (tangents != null)
                    {
                        float3 tt = Normalize(new float3((float)tangents[3 * loop], (float)tangents[3 * loop + 1], (float)tangents[3 * loop + 2]), new float3(1, 0, 0));
                        float w = 1f;
                        if (binormals != null)
                        {
                            float3 bb = new float3((float)binormals[3 * loop], (float)binormals[3 * loop + 1], (float)binormals[3 * loop + 2]);
                            w = math.dot(math.cross(n, tt), bb) < 0f ? -1f : 1f;
                        }
                        t = new float4(tt, w);
                    }
                    else
                    {
                        float3 reference = math.abs(n.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
                        t = new float4(Normalize(math.cross(reference, n), new float3(1, 0, 0)), 1f);
                    }
                    var key = (v, n, uv, t);
                    if (!renderOfKey.TryGetValue(key, out uint r))
                    {
                        r = (uint)renderVertices.Count;
                        renderOfKey.Add(key, r);
                        renderVertices.Add(new RenderVertex { position = positions[v], normal = n, uv0 = uv, tangent = t });
                        renderTopology.Add(v);
                    }
                    corner[k] = r;
                }
                int material = polygonMaterial != null ? polygonMaterial[p] : 0;
                if (poly.Length != 3) nonTri++;
                for (int k = 1; k + 1 < poly.Length; k++) triangles.Add((corner[0], corner[k], corner[k + 1], material));
            }
            g.NonTrianglePolygons = nonTri;
            if (nonTri > 0) g.Notes.Add(nonTri + " non-triangle polygon(s) fan-triangulated on import");
            g.RenderVertices = renderVertices.Count;

            var mesh = new SyntheticMesh { Vertices = renderVertices.ToArray(), TopologyOfVertex = renderTopology.ToArray(), TopologyVertexCount = cp };
            int materialCount = 0;
            foreach (var t in triangles) materialCount = Math.Max(materialCount, t.material + 1);
            var indices = new List<uint>(3 * triangles.Count);
            for (int m = 0; m < materialCount; m++)
            {
                int before = indices.Count;
                foreach (var t in triangles) if (t.material == m) { indices.Add(t.a); indices.Add(t.b); indices.Add(t.c); }
                if (indices.Count > before) mesh.SubmeshIndexCounts.Add(indices.Count - before);
            }
            mesh.Indices = indices.ToArray();
            g.Mesh = mesh;
        }

        static float3 Normalize(float3 v, float3 fallback)
        {
            float l2 = math.lengthsq(v);
            return l2 > 1e-20f ? v / (float)Math.Sqrt(l2) : fallback;
        }

        /// <summary>Reads a per-polygon-vertex layer as a flat double array (components per corner), resolving IndexToDirect.</summary>
        static double[] ReadLayer(FbxNode geometry, string layer, string dataName, string indexName, out string mapping, int corners, int components)
        {
            mapping = null;
            var node = geometry.Child(layer);
            if (node == null) return null;
            mapping = node.Child("MappingInformationType")?.PropertyString(0);
            string reference = node.Child("ReferenceInformationType")?.PropertyString(0);
            var data = node.Child(dataName)?.Property<double[]>(0);
            if (data == null) return null;
            if (mapping != "ByPolygonVertex") { mapping += " (unsupported, ignored)"; return null; }
            if (reference == "IndexToDirect")
            {
                var index = node.Child(indexName)?.Property<int[]>(0);
                if (index == null) return null;
                var resolved = new double[corners * components];
                for (int i = 0; i < corners && i < index.Length; i++)
                    for (int c = 0; c < components; c++) resolved[i * components + c] = data[index[i] * components + c];
                return resolved;
            }
            if (data.Length < corners * components) return null;
            return data;
        }
    }
}
