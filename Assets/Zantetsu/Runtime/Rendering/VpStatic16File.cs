using System.IO;
using System.Text;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Registration-only intake of offline-packed geometry. No Mesh, attribute encode, positional weld or persistent
    /// cache is created. The caller owns/closes the stream; temporary managed 16B arrays are copied into storage and
    /// are not retained. Local UInt32 indices are rebased once by storage, not by the file producer.
    /// </summary>
    public static class VpStatic16File
    {
        public const uint Magic = 0x36315056; // little-endian "VP16"
        public const int Version = 1;
        public const int HeaderBytes = 28;

        public static bool TryAppendCuttable(Stream source, VpCpuGeometryStorage storage,
            out VpStoredGeometry geometry, out string failure)
        {
            geometry = default;
            failure = "invalid stream/storage";
            if (source == null || storage == null || !source.CanRead || !source.CanSeek) return false;
            try
            {
                long remaining = source.Length - source.Position;
                failure = "invalid header/layout";
                if (remaining < HeaderBytes) return false;
                using var reader = new BinaryReader(source, Encoding.UTF8, true);
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != 16) return false;
                int vc = reader.ReadInt32(), ic = reader.ReadInt32(), tc = reader.ReadInt32(), sc = reader.ReadInt32();
                if (vc <= 0 || ic <= 0 || ic % 3 != 0 || tc <= 0 || sc <= 0
                    || vc > storage.VertexCapacity || ic > storage.IndexCapacity || sc > storage.SubmeshCapacity) return false;
                long expected = HeaderBytes + 20L * vc + 4L * ic + 12L * sc;
                failure = "payload length mismatch";
                if (remaining != expected) return false; // Reject truncation/trailing data before allocating arrays.
                var vertices = new VpRenderVertex[vc];
                for (int i = 0; i < vc; i++)
#if VP_DIAGNOSTIC_LEGACY32
                {
                    Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    sbyte x = reader.ReadSByte(), y = reader.ReadSByte();
                    byte u = reader.ReadByte(), v = reader.ReadByte();
                    if (x == -128 || y == -128) return false;
                    var e = new Unity.Mathematics.float2(x, y) / 127f;
                    var n = new Unity.Mathematics.float3(e, 1f - Unity.Mathematics.math.abs(e.x) - Unity.Mathematics.math.abs(e.y));
                    if (n.z < 0) n.xy = (1f - Unity.Mathematics.math.abs(n.yx)) * Unity.Mathematics.math.select(new Unity.Mathematics.float2(-1f), new Unity.Mathematics.float2(1f), e >= 0f);
                    vertices[i] = new VpRenderVertex { position = position, normal = Unity.Mathematics.math.normalize(n), uv0 = new Vector2((u + .5f) / 256f, (v + .5f) / 256f) };
                }
#else
                    vertices[i] = new VpRenderVertex {
                        position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        normalX = reader.ReadSByte(), normalY = reader.ReadSByte(), u = reader.ReadByte(), v = reader.ReadByte()
                    };
#endif
                var indices = new uint[ic];
                for (int i = 0; i < ic; i++) indices[i] = reader.ReadUInt32();
                var topology = new int[vc];
                for (int i = 0; i < vc; i++) topology[i] = reader.ReadInt32();
                var submeshes = new VpGeometrySubmesh[sc];
                for (int i = 0; i < sc; i++) submeshes[i] = new VpGeometrySubmesh(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                if (!storage.TryAppendCuttable(vertices, indices, topology, tc, submeshes, out geometry, out var verdict))
                {
                    failure = verdict.Accepted ? "storage refused attributes/capacity" : verdict.ToString();
                    return false;
                }
                failure = null;
                return true;
            }
            catch (EndOfStreamException) { failure = "truncated payload"; return false; }
        }
    }
}
