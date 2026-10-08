using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07): the VP geometry of a scene's ordinary meshes, each converted once by
    /// <see cref="VpMeshConverter"/> -- the mesh's own vertices, its own vertex numbers and its submeshes in order --
    /// and kept by a key a Player can read off a mesh it cannot read into.
    /// <para>
    /// **Why it is kept in a file.** The city's models are imported without Read/Write, so a Player cannot read their
    /// vertices: the conversion is made where they can be read (the Editor, a test) and written out
    /// (<see cref="Write"/>); the Player reads the file back (<see cref="TryRead"/>) while it prepares. What is written
    /// is the converter's output as it is, byte for byte, with the vertex stride it was made with: a file of another
    /// stride is refused, never re-encoded.
    /// </para>
    /// <para>
    /// **The key** is what a mesh says of itself without being read: its name, vertex count, each submesh's index
    /// count, and its bounds. Two different meshes with one key are refused when the second is added, so a key found
    /// names one geometry.
    /// </para>
    /// Not a product importer, and no cut input: nothing here is welded, repaired or given a topology.
    /// </summary>
    public sealed class SceneDrawMeshBank
    {
        public const uint Magic = 0x43445056;   // little-endian "VPDC"
        public const int Version = 1;

        /// <summary>One mesh's converted geometry: indices are the mesh's own vertex numbers.</summary>
        public sealed class Entry
        {
            public string key, name;
            public VpRenderVertex[] vertices;
            public uint[] indices;
            public int[] submeshIndexStart, submeshIndexCount;

            // Every vertex its own topology vertex: a displayed geometry needs the map, nothing reads more into it.
            private int[] _identity;

            internal int[] Identity
            {
                get
                {
                    if (_identity == null)
                    {
                        _identity = new int[vertices.Length];
                        for (int i = 0; i < _identity.Length; i++) _identity[i] = i;
                    }

                    return _identity;
                }
            }
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<Entry> _ordered = new List<Entry>();

        public int Count => _ordered.Count;
        public IReadOnlyList<Entry> Entries => _ordered;
        public long VertexCount { get; private set; }
        public long IndexCount { get; private set; }

        /// <summary>What a mesh says of itself without being read (see the class).</summary>
        public static string KeyOf(Mesh mesh)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var key = new StringBuilder(96);
            key.Append(mesh.name).Append('|').Append(mesh.vertexCount).Append('|').Append(mesh.subMeshCount).Append('|');
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                SubMeshDescriptor d = mesh.GetSubMesh(s);
                key.Append(s == 0 ? "" : ",").Append(d.indexCount);
            }

            Bounds b = mesh.bounds;
            key.Append('|').Append(b.center.x.ToString("R", inv)).Append(',').Append(b.center.y.ToString("R", inv)).Append(',').Append(b.center.z.ToString("R", inv))
                .Append('|').Append(b.size.x.ToString("R", inv)).Append(',').Append(b.size.y.ToString("R", inv)).Append(',').Append(b.size.z.ToString("R", inv));
            return key.ToString();
        }

        public bool TryGet(Mesh mesh, out Entry entry)
        {
            entry = null;
            return mesh != null && _entries.TryGetValue(KeyOf(mesh), out entry);
        }

        /// <summary>
        /// Converts <paramref name="mesh"/> and keeps it, where its vertices can be read. True, adding nothing, when
        /// this very geometry is already kept. False with the reason when the mesh cannot be read here, the converter
        /// rejects it (no normals, a submesh that is not a triangle list, a non-finite value, a zero normal, a UV
        /// outside the palette's range), or another geometry is kept under its key.
        /// </summary>
        public bool TryAdd(Mesh mesh, out string refusal)
        {
            refusal = null;
            if (mesh == null)
            {
                refusal = "no mesh";
                return false;
            }

            // A Player cannot read a mesh imported without Read/Write; the Editor reads every mesh.
            if (!mesh.isReadable && !Application.isEditor)
            {
                refusal = "the mesh '" + mesh.name + "' cannot be read here (imported without Read/Write)";
                return false;
            }

            int vertexCount = mesh.vertexCount;
            long indexCount = 0;
            var starts = new int[mesh.subMeshCount];
            var counts = new int[mesh.subMeshCount];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                SubMeshDescriptor d = mesh.GetSubMesh(s);
                if (d.topology != MeshTopology.Triangles)
                {
                    refusal = "the mesh '" + mesh.name + "': submesh " + s + " is " + d.topology + ", not a triangle list";
                    return false;
                }

                starts[s] = (int)indexCount;
                counts[s] = d.indexCount;
                indexCount += d.indexCount;
            }

            if (vertexCount <= 0 || indexCount <= 0 || indexCount > int.MaxValue)
            {
                refusal = "the mesh '" + mesh.name + "' has " + vertexCount + " vertices and " + indexCount + " indices";
                return false;
            }

            var entry = new Entry { key = KeyOf(mesh), name = mesh.name, submeshIndexStart = starts, submeshIndexCount = counts };
            using (var vertices = new NativeArray<VpRenderVertex>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            using (var indices = new NativeArray<uint>((int)indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                if (!VpMeshConverter.TryConvert(mesh, vertices, indices, out int convertedVertices, out int convertedIndices)
                    || convertedVertices != vertexCount || convertedIndices != indexCount)
                {
                    refusal = "the mesh '" + mesh.name + "' was rejected by the VP mesh conversion (no normals, a non-finite value, a zero normal, or a UV outside 0..1)";
                    return false;
                }

                entry.vertices = vertices.ToArray();
                entry.indices = indices.ToArray();
            }

            for (int i = 0; i < entry.indices.Length; i++)
            {
                if (entry.indices[i] >= (uint)vertexCount)
                {
                    refusal = "the mesh '" + mesh.name + "': index " + i + " names vertex " + entry.indices[i] + " of " + vertexCount;
                    return false;
                }
            }

            return TryKeep(entry, out refusal);
        }

        private bool TryKeep(Entry entry, out string refusal)
        {
            refusal = null;
            if (_entries.TryGetValue(entry.key, out Entry kept))
            {
                if (SameGeometry(kept, entry))
                {
                    return true;
                }

                refusal = "another geometry is kept under the key of the mesh '" + entry.name + "' (" + entry.key + ")";
                return false;
            }

            _entries.Add(entry.key, entry);
            _ordered.Add(entry);
            VertexCount += entry.vertices.Length;
            IndexCount += entry.indices.Length;
            return true;
        }

        private static bool SameGeometry(Entry a, Entry b)
        {
            return a.vertices.Length == b.vertices.Length && a.indices.Length == b.indices.Length
                && MemoryMarshal.AsBytes(a.vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.vertices.AsSpan()))
                && a.indices.AsSpan().SequenceEqual(b.indices)
                && a.submeshIndexCount.AsSpan().SequenceEqual(b.submeshIndexCount);
        }

        /// <summary>Writes every kept geometry, in the order it was added. The caller owns the stream.</summary>
        public void Write(Stream destination)
        {
            using (var writer = new BinaryWriter(destination, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(VpRenderVertex.Stride);
                writer.Write(_ordered.Count);
                foreach (Entry entry in _ordered)
                {
                    writer.Write(entry.key);
                    writer.Write(entry.name);
                    writer.Write(entry.vertices.Length);
                    writer.Write(entry.indices.Length);
                    writer.Write(entry.submeshIndexCount.Length);
                    for (int s = 0; s < entry.submeshIndexCount.Length; s++)
                    {
                        writer.Write(entry.submeshIndexStart[s]);
                        writer.Write(entry.submeshIndexCount[s]);
                    }

                    writer.Write(MemoryMarshal.AsBytes(entry.vertices.AsSpan()));
                    writer.Write(MemoryMarshal.AsBytes(entry.indices.AsSpan()));
                }
            }
        }

        /// <summary>
        /// Reads what <see cref="Write"/> wrote. False with the reason, and no bank, when the file is not one of
        /// these, was made with another vertex stride, is cut short, carries anything after its last geometry, or
        /// holds a geometry whose counts, submeshes or indices do not hold together.
        /// </summary>
        public static bool TryRead(Stream source, out SceneDrawMeshBank bank, out string failure)
        {
            bank = null;
            failure = null;
            var made = new SceneDrawMeshBank();
            try
            {
                using (var reader = new BinaryReader(source, Encoding.UTF8, true))
                {
                    if (reader.ReadUInt32() != Magic)
                    {
                        failure = "not a scene draw geometry file";
                        return false;
                    }

                    int version = reader.ReadInt32();
                    int stride = reader.ReadInt32();
                    if (version != Version || stride != VpRenderVertex.Stride)
                    {
                        failure = "version " + version + " with a vertex stride of " + stride + "; this build reads version " + Version + " at " + VpRenderVertex.Stride;
                        return false;
                    }

                    int count = reader.ReadInt32();
                    if (count < 0)
                    {
                        failure = "a negative geometry count";
                        return false;
                    }

                    for (int e = 0; e < count; e++)
                    {
                        var entry = new Entry { key = reader.ReadString(), name = reader.ReadString() };
                        int vertexCount = reader.ReadInt32(), indexCount = reader.ReadInt32(), submeshCount = reader.ReadInt32();
                        long left = source.Length - source.Position;
                        if (vertexCount <= 0 || indexCount <= 0 || submeshCount <= 0
                            || 8L * submeshCount + (long)vertexCount * VpRenderVertex.Stride + 4L * indexCount > left)
                        {
                            failure = "geometry " + e + " ('" + entry.name + "') does not fit the file: " + vertexCount + " vertices, " + indexCount + " indices, " + submeshCount + " submeshes, " + left + " bytes left";
                            return false;
                        }

                        entry.submeshIndexStart = new int[submeshCount];
                        entry.submeshIndexCount = new int[submeshCount];
                        int covered = 0;
                        for (int s = 0; s < submeshCount; s++)
                        {
                            entry.submeshIndexStart[s] = reader.ReadInt32();
                            entry.submeshIndexCount[s] = reader.ReadInt32();
                            if (entry.submeshIndexStart[s] != covered || entry.submeshIndexCount[s] < 0)
                            {
                                failure = "geometry " + e + " ('" + entry.name + "'): its submeshes do not cover its indices in order";
                                return false;
                            }

                            covered += entry.submeshIndexCount[s];
                        }

                        if (covered != indexCount)
                        {
                            failure = "geometry " + e + " ('" + entry.name + "'): its submeshes cover " + covered + " of " + indexCount + " indices";
                            return false;
                        }

                        entry.vertices = new VpRenderVertex[vertexCount];
                        entry.indices = new uint[indexCount];
                        if (!ReadAll(reader, MemoryMarshal.AsBytes(entry.vertices.AsSpan())) || !ReadAll(reader, MemoryMarshal.AsBytes(entry.indices.AsSpan())))
                        {
                            failure = "geometry " + e + " ('" + entry.name + "') is cut short";
                            return false;
                        }

                        for (int i = 0; i < indexCount; i++)
                        {
                            if (entry.indices[i] >= (uint)vertexCount)
                            {
                                failure = "geometry " + e + " ('" + entry.name + "'): index " + i + " names vertex " + entry.indices[i] + " of " + vertexCount;
                                return false;
                            }
                        }

                        if (!made.TryKeep(entry, out failure))
                        {
                            return false;
                        }
                    }

                    if (source.Position != source.Length)
                    {
                        failure = (source.Length - source.Position) + " bytes after the last geometry";
                        return false;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                failure = "the file is cut short";
                return false;
            }

            bank = made;
            return true;
        }

        private static bool ReadAll(BinaryReader reader, Span<byte> into)
        {
            while (into.Length > 0)
            {
                int read = reader.Read(into);
                if (read <= 0)
                {
                    return false;
                }

                into = into.Slice(read);
            }

            return true;
        }
    }
}
