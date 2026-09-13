using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Core.Geometry
{
    public enum ZcgGeometryKind : byte
    {
        TriangleMesh = 1,
        ConvexSet = 2,
    }

    public readonly struct ZcgPosition
    {
        public ZcgPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
    }

    public readonly struct ZcgTriangle
    {
        public ZcgTriangle(uint i0, uint i1, uint i2) { I0 = i0; I1 = i1; I2 = i2; }
        public uint I0 { get; }
        public uint I1 { get; }
        public uint I2 { get; }
    }

    public sealed class ZcgHull
    {
        public ZcgHull(ZcgPosition[] positions, uint[][] faces)
        {
            Positions = positions ?? throw new ArgumentNullException(nameof(positions));
            Faces = faces ?? throw new ArgumentNullException(nameof(faces));
        }

        public ZcgPosition[] Positions { get; }
        public uint[][] Faces { get; }
    }

    public sealed class ZcgDocument
    {
        internal ZcgDocument(ZcgPosition[] positions, ZcgTriangle[] triangles)
        {
            Kind = ZcgGeometryKind.TriangleMesh;
            Positions = positions;
            Triangles = triangles;
            Hulls = Array.Empty<ZcgHull>();
        }

        internal ZcgDocument(ZcgHull[] hulls)
        {
            Kind = ZcgGeometryKind.ConvexSet;
            Positions = Array.Empty<ZcgPosition>();
            Triangles = Array.Empty<ZcgTriangle>();
            Hulls = hulls;
        }

        public ZcgGeometryKind Kind { get; }
        public ZcgPosition[] Positions { get; }
        public ZcgTriangle[] Triangles { get; }
        public ZcgHull[] Hulls { get; }
    }

    public readonly struct ZcgDecodeLimits
    {
        public ZcgDecodeLimits(int maximumFileBytes, int maximumTrianglePositions,
            int maximumTriangles, int maximumHulls, int maximumPositionsPerHull)
        {
            MaximumFileBytes = maximumFileBytes;
            MaximumTrianglePositions = maximumTrianglePositions;
            MaximumTriangles = maximumTriangles;
            MaximumHulls = maximumHulls;
            MaximumPositionsPerHull = maximumPositionsPerHull;
        }

        public int MaximumFileBytes { get; }
        public int MaximumTrianglePositions { get; }
        public int MaximumTriangles { get; }
        public int MaximumHulls { get; }
        public int MaximumPositionsPerHull { get; }

        public static ZcgDecodeLimits Phase02 => new ZcgDecodeLimits(
            32 * 1024 * 1024, 200000, 200000, 1, 255);
    }

    /// <summary>Bounded ZCG v1 reader, canonical reserializer, and shared numeric gates.</summary>
    public static class ZcgGeometryCodec
    {
        private const int HeaderSize = 16;

        public static ZcgDocument Read(byte[] data, ZcgDecodeLimits limits,
            double absoluteEpsilonMeters = 0.000001, double relativeEpsilon = 0.000001)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (limits.MaximumFileBytes < HeaderSize || data.Length < HeaderSize ||
                data.Length > limits.MaximumFileBytes)
                throw new InvalidDataException("ZCG file length is outside limits.");
            if (data[0] != 'Z' || data[1] != 'C' || data[2] != 'G' || data[3] != '1')
                throw new InvalidDataException("Invalid ZCG magic.");
            if (data[5] != 0 || data[6] != 0 || data[7] != 0)
                throw new InvalidDataException("ZCG reserved bytes must be zero.");
            ZcgGeometryKind kind = (ZcgGeometryKind)data[4];
            if (kind != ZcgGeometryKind.TriangleMesh && kind != ZcgGeometryKind.ConvexSet)
                throw new InvalidDataException("Unknown ZCG geometry kind.");
            ulong payloadLength = ReadUInt64(data, 8);
            if (payloadLength != (ulong)(data.Length - HeaderSize))
                throw new InvalidDataException("ZCG payload length mismatch.");

            Reader reader = new Reader(data, HeaderSize);
            ZcgDocument document = kind == ZcgGeometryKind.TriangleMesh
                ? ReadTriangleMesh(reader, limits, absoluteEpsilonMeters, relativeEpsilon)
                : ReadConvexSet(reader, limits, absoluteEpsilonMeters, relativeEpsilon);
            if (reader.Offset != data.Length)
                throw new InvalidDataException("ZCG contains trailing data.");
            byte[] roundTrip = WriteCanonical(document);
            if (!BytesEqual(data, roundTrip))
                throw new InvalidDataException("ZCG bytes are not canonical.");
            return document;
        }

        public static byte[] WriteCanonical(ZcgDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            using (MemoryStream payload = new MemoryStream())
            {
                if (document.Kind == ZcgGeometryKind.TriangleMesh)
                {
                    WriteUInt32(payload, CheckedUInt32(document.Positions.Length));
                    WriteUInt32(payload, CheckedUInt32(document.Triangles.Length));
                    WritePositions(payload, document.Positions);
                    foreach (ZcgTriangle triangle in document.Triangles)
                    {
                        WriteUInt32(payload, triangle.I0);
                        WriteUInt32(payload, triangle.I1);
                        WriteUInt32(payload, triangle.I2);
                    }
                }
                else if (document.Kind == ZcgGeometryKind.ConvexSet)
                {
                    WriteUInt32(payload, CheckedUInt32(document.Hulls.Length));
                    foreach (ZcgHull hull in document.Hulls)
                    {
                        WriteUInt32(payload, CheckedUInt32(hull.Positions.Length));
                        WriteUInt32(payload, CheckedUInt32(hull.Faces.Length));
                        WritePositions(payload, hull.Positions);
                        foreach (uint[] face in hull.Faces)
                        {
                            WriteUInt32(payload, CheckedUInt32(face.Length));
                            foreach (uint index in face) WriteUInt32(payload, index);
                        }
                    }
                }
                else throw new InvalidDataException("Unknown ZCG geometry kind.");

                byte[] body = payload.ToArray();
                using (MemoryStream output = new MemoryStream(HeaderSize + body.Length))
                {
                    output.WriteByte((byte)'Z'); output.WriteByte((byte)'C');
                    output.WriteByte((byte)'G'); output.WriteByte((byte)'1');
                    output.WriteByte((byte)document.Kind);
                    output.WriteByte(0); output.WriteByte(0); output.WriteByte(0);
                    WriteUInt64(output, (ulong)body.Length);
                    output.Write(body, 0, body.Length);
                    return output.ToArray();
                }
            }
        }

        public static string ComputeSha256(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(data);
                char[] chars = new char[digest.Length * 2];
                const string hex = "0123456789abcdef";
                for (int i = 0; i < digest.Length; i++)
                {
                    chars[i * 2] = hex[digest[i] >> 4];
                    chars[i * 2 + 1] = hex[digest[i] & 15];
                }
                return new string(chars);
            }
        }

        private static ZcgDocument ReadTriangleMesh(Reader reader, ZcgDecodeLimits limits,
            double absolute, double relative)
        {
            uint positionCountRaw = reader.UInt32("PositionCount");
            uint triangleCountRaw = reader.UInt32("TriangleCount");
            int positionCount = BoundedCount(positionCountRaw, 1, limits.MaximumTrianglePositions, "PositionCount");
            int triangleCount = BoundedCount(triangleCountRaw, 1, limits.MaximumTriangles, "TriangleCount");
            RequireRemaining(reader, (long)positionCount * 12L + (long)triangleCount * 12L,
                "TriangleMesh arrays");
            ZcgPosition[] positions = ReadPositions(reader, positionCount, "position");
            RequireCanonicalPositions(positions);
            ZcgTriangle[] triangles = new ZcgTriangle[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                ZcgTriangle value = new ZcgTriangle(reader.UInt32("triangle index"),
                    reader.UInt32("triangle index"), reader.UInt32("triangle index"));
                RequireTriangleCanonical(value, positions.Length, i == 0 ? null : (ZcgTriangle?)triangles[i - 1]);
                triangles[i] = value;
            }
            ValidateTriangles(positions, triangles, absolute, relative);
            return new ZcgDocument(positions, triangles);
        }

        private static ZcgDocument ReadConvexSet(Reader reader, ZcgDecodeLimits limits,
            double absolute, double relative)
        {
            int hullCount = BoundedCount(reader.UInt32("HullCount"), 1,
                Math.Min(1, limits.MaximumHulls), "HullCount");
            ZcgHull[] hulls = new ZcgHull[hullCount];
            byte[] previousHull = null;
            for (int h = 0; h < hullCount; h++)
            {
                int positionCount = BoundedCount(reader.UInt32("PositionCount"), 4,
                    Math.Min(255, limits.MaximumPositionsPerHull), "convex PositionCount");
                int faceCount = BoundedCount(reader.UInt32("FaceCount"), 0,
                    checked(2 * positionCount - 4), "convex FaceCount");
                RequireRemaining(reader, (long)positionCount * 12L, "convex positions");
                ZcgPosition[] positions = ReadPositions(reader, positionCount, "hull position");
                RequireCanonicalPositions(positions);
                uint[][] faces = new uint[faceCount][];
                long totalIndices = 0;
                for (int f = 0; f < faceCount; f++)
                {
                    uint countRaw = reader.UInt32("face IndexCount");
                    if (countRaw > int.MaxValue) throw new InvalidDataException("Face IndexCount exceeds limit.");
                    int count = (int)countRaw;
                    if (count < 3) throw new InvalidDataException("Face must contain at least three indices.");
                    totalIndices += count;
                    if (totalIndices > 6 * positionCount - 12)
                        throw new InvalidDataException("Convex face index count exceeds 6V-12.");
                    RequireRemaining(reader, (long)count * 4L, "face indices");
                    uint[] face = new uint[count];
                    for (int i = 0; i < count; i++) face[i] = reader.UInt32("face index");
                    RequireFaceCanonical(face, positionCount, f == 0 ? null : faces[f - 1]);
                    faces[f] = face;
                }
                ZcgHull hull = new ZcgHull(positions, faces);
                ValidateHull(hull, absolute, relative);
                byte[] hullBytes = WriteHull(hull);
                if (previousHull != null && CompareBytes(previousHull, hullBytes) >= 0)
                    throw new InvalidDataException("Convex hull records are not strictly canonical.");
                previousHull = hullBytes;
                hulls[h] = hull;
            }
            return new ZcgDocument(hulls);
        }

        private static void ValidateTriangles(ZcgPosition[] positions, ZcgTriangle[] triangles,
            double absolute, double relative)
        {
            Epsilon epsilon = CalculateEpsilon(positions, absolute, relative);
            for (int i = 0; i < triangles.Length; i++)
            {
                ZcgPosition v0 = positions[triangles[i].I0];
                ZcgPosition v1 = positions[triangles[i].I1];
                ZcgPosition v2 = positions[triangles[i].I2];
                D3 cross = Cross(Subtract(v1, v0), Subtract(v2, v0));
                double twiceArea = Length(cross);
                if (!IsFinite(twiceArea) || !(twiceArea > epsilon.Area))
                    throw new InvalidDataException("Triangle is degenerate.");
            }
        }

        private static void ValidateHull(ZcgHull hull, double absolute, double relative)
        {
            Epsilon epsilon = CalculateEpsilon(hull.Positions, absolute, relative);
            var edges = new Dictionary<ulong, List<DirectedEdge>>();
            foreach (uint[] face in hull.Faces)
            {
                ZcgPosition v0 = hull.Positions[face[0]];
                D3 normal = default;
                double magnitude = 0.0;
                bool found = false;
                for (int i = 1; i < face.Length - 1; i++)
                {
                    D3 raw = Cross(Subtract(hull.Positions[face[i]], v0),
                        Subtract(hull.Positions[face[i + 1]], v0));
                    D3 candidate = new D3(-raw.X, -raw.Y, -raw.Z);
                    magnitude = Length(candidate);
                    if (magnitude > epsilon.Area)
                    {
                        normal = new D3(candidate.X / magnitude, candidate.Y / magnitude, candidate.Z / magnitude);
                        found = true;
                        break;
                    }
                }
                if (!found) throw new InvalidDataException("Convex face is degenerate.");
                double planeOffset = -Dot(normal, v0);
                foreach (uint index in face)
                {
                    double distance = Dot(normal, hull.Positions[index]) + planeOffset;
                    if (!IsFinite(distance) || Math.Abs(distance) > epsilon.Distance)
                        throw new InvalidDataException("Convex face is non-planar.");
                }
                foreach (ZcgPosition position in hull.Positions)
                {
                    double distance = Dot(normal, position) + planeOffset;
                    if (!IsFinite(distance) || distance > epsilon.Distance)
                        throw new InvalidDataException("Convex face is inward or hull is non-convex.");
                }
                for (int i = 0; i < face.Length; i++)
                {
                    uint start = face[i]; uint end = face[(i + 1) % face.Length];
                    uint low = Math.Min(start, end); uint high = Math.Max(start, end);
                    ulong key = ((ulong)low << 32) | high;
                    if (!edges.TryGetValue(key, out List<DirectedEdge> uses))
                    {
                        uses = new List<DirectedEdge>(2); edges.Add(key, uses);
                    }
                    uses.Add(new DirectedEdge(start, end));
                }
            }
            foreach (KeyValuePair<ulong, List<DirectedEdge>> item in edges)
            {
                List<DirectedEdge> uses = item.Value;
                if (uses.Count != 2 || uses[0].Start != uses[1].End || uses[0].End != uses[1].Start)
                    throw new InvalidDataException("Convex edge is not closed with opposite orientation.");
            }

            Bounds bounds = CalculateBounds(hull.Positions);
            D3 center = new D3(bounds.MinX + (bounds.MaxX - bounds.MinX) * 0.5,
                bounds.MinY + (bounds.MaxY - bounds.MinY) * 0.5,
                bounds.MinZ + (bounds.MaxZ - bounds.MinZ) * 0.5);
            double volume = 0.0;
            foreach (uint[] face in hull.Faces)
            {
                D3 a = Subtract(hull.Positions[face[0]], center);
                for (int i = 1; i < face.Length - 1; i++)
                {
                    D3 b = Subtract(hull.Positions[face[i]], center);
                    D3 c = Subtract(hull.Positions[face[i + 1]], center);
                    double term = -Dot(a, Cross(b, c)) / 6.0;
                    volume = volume + term;
                }
            }
            if (!IsFinite(volume) || !(volume > epsilon.Volume))
                throw new InvalidDataException("Convex volume must be positive.");
        }

        private static void RequireCanonicalPositions(ZcgPosition[] positions)
        {
            for (int i = 1; i < positions.Length; i++)
                if (ComparePosition(positions[i - 1], positions[i]) >= 0)
                    throw new InvalidDataException("Positions are not strictly canonical.");
        }

        private static void RequireTriangleCanonical(ZcgTriangle value, int count, ZcgTriangle? previous)
        {
            if (value.I0 >= count || value.I1 >= count || value.I2 >= count ||
                value.I0 == value.I1 || value.I0 == value.I2 || value.I1 == value.I2)
                throw new InvalidDataException("Triangle index is invalid.");
            if (CompareTriple(value.I0, value.I1, value.I2, value.I1, value.I2, value.I0) > 0 ||
                CompareTriple(value.I0, value.I1, value.I2, value.I2, value.I0, value.I1) > 0)
                throw new InvalidDataException("Triangle cyclic rotation is non-canonical.");
            if (previous.HasValue && CompareTriangle(previous.Value, value) >= 0)
                throw new InvalidDataException("Triangles are not strictly canonical.");
        }

        private static void RequireFaceCanonical(uint[] face, int count, uint[] previous)
        {
            var seen = new HashSet<uint>();
            foreach (uint index in face)
                if (index >= count || !seen.Add(index)) throw new InvalidDataException("Face index is invalid.");
            for (int rotation = 1; rotation < face.Length; rotation++)
                if (CompareRotation(face, 0, rotation) > 0)
                    throw new InvalidDataException("Face cyclic rotation is non-canonical.");
            if (previous != null && CompareFace(previous, face) >= 0)
                throw new InvalidDataException("Faces are not strictly canonical.");
        }

        private static Epsilon CalculateEpsilon(ZcgPosition[] positions, double absolute, double relative)
        {
            if (!IsFinite(absolute) || absolute < 0.0 || !IsFinite(relative) || relative < 0.0)
                throw new InvalidDataException("Epsilon must be finite and nonnegative.");
            Bounds b = CalculateBounds(positions);
            double dx = b.MaxX - b.MinX; double dy = b.MaxY - b.MinY; double dz = b.MaxZ - b.MinZ;
            double xy = dx * dx + dy * dy;
            double diagonal = Math.Sqrt(xy + dz * dz);
            if (!IsFinite(diagonal) || !(diagonal > 0.0))
                throw new InvalidDataException("Bounds diagonal must be positive and finite.");
            double distance = Math.Max(absolute, diagonal * relative);
            double area = distance * distance; double volume = area * distance;
            if (!IsFinite(distance) || !IsFinite(area) || !IsFinite(volume))
                throw new InvalidDataException("Numeric epsilon is non-finite.");
            return new Epsilon(distance, area, volume);
        }

        private static Bounds CalculateBounds(ZcgPosition[] positions)
        {
            if (positions.Length == 0) throw new InvalidDataException("Geometry contains no positions.");
            double minX = positions[0].X, minY = positions[0].Y, minZ = positions[0].Z;
            double maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 1; i < positions.Length; i++)
            {
                minX = Math.Min(minX, positions[i].X); minY = Math.Min(minY, positions[i].Y); minZ = Math.Min(minZ, positions[i].Z);
                maxX = Math.Max(maxX, positions[i].X); maxY = Math.Max(maxY, positions[i].Y); maxZ = Math.Max(maxZ, positions[i].Z);
            }
            return new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
        }

        private static D3 Subtract(ZcgPosition a, ZcgPosition b) =>
            new D3((double)a.X - b.X, (double)a.Y - b.Y, (double)a.Z - b.Z);
        private static D3 Subtract(ZcgPosition a, D3 b) =>
            new D3((double)a.X - b.X, (double)a.Y - b.Y, (double)a.Z - b.Z);
        private static double Dot(D3 a, ZcgPosition b)
        {
            double xy = a.X * b.X + a.Y * b.Y;
            return xy + a.Z * b.Z;
        }
        private static double Dot(D3 a, D3 b)
        {
            double xy = a.X * b.X + a.Y * b.Y;
            return xy + a.Z * b.Z;
        }
        private static D3 Cross(D3 a, D3 b) => new D3(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        private static double Length(D3 value)
        {
            double xy = value.X * value.X + value.Y * value.Y;
            return Math.Sqrt(xy + value.Z * value.Z);
        }

        private static ZcgPosition[] ReadPositions(Reader reader, int count, string label)
        {
            ZcgPosition[] values = new ZcgPosition[count];
            for (int i = 0; i < count; i++)
            {
                float x = reader.Single(label); float y = reader.Single(label); float z = reader.Single(label);
                values[i] = new ZcgPosition(x, y, z);
            }
            return values;
        }

        private static void WritePositions(Stream stream, ZcgPosition[] positions)
        {
            foreach (ZcgPosition value in positions)
            {
                WriteSingle(stream, value.X); WriteSingle(stream, value.Y); WriteSingle(stream, value.Z);
            }
        }

        private static byte[] WriteHull(ZcgHull hull)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                WriteUInt32(stream, CheckedUInt32(hull.Positions.Length));
                WriteUInt32(stream, CheckedUInt32(hull.Faces.Length));
                WritePositions(stream, hull.Positions);
                foreach (uint[] face in hull.Faces)
                {
                    WriteUInt32(stream, CheckedUInt32(face.Length));
                    foreach (uint index in face) WriteUInt32(stream, index);
                }
                return stream.ToArray();
            }
        }

        private static int BoundedCount(uint value, int minimum, int maximum, string label)
        {
            if (maximum < minimum || value < minimum || value > maximum)
                throw new InvalidDataException(label + " exceeds limit.");
            return (int)value;
        }

        private static void RequireRemaining(Reader reader, long bytes, string label)
        {
            if (bytes < 0 || bytes > reader.Remaining) throw new InvalidDataException(label + " exceeds remaining payload.");
        }

        private static int ComparePosition(ZcgPosition a, ZcgPosition b)
        {
            int c = a.X.CompareTo(b.X); if (c != 0) return c;
            c = a.Y.CompareTo(b.Y); return c != 0 ? c : a.Z.CompareTo(b.Z);
        }
        private static int CompareTriangle(ZcgTriangle a, ZcgTriangle b) =>
            CompareTriple(a.I0, a.I1, a.I2, b.I0, b.I1, b.I2);
        private static int CompareTriple(uint a0, uint a1, uint a2, uint b0, uint b1, uint b2)
        {
            int c = a0.CompareTo(b0); if (c != 0) return c;
            c = a1.CompareTo(b1); return c != 0 ? c : a2.CompareTo(b2);
        }
        private static int CompareRotation(uint[] face, int a, int b)
        {
            for (int i = 0; i < face.Length; i++)
            {
                int c = face[(a + i) % face.Length].CompareTo(face[(b + i) % face.Length]);
                if (c != 0) return c;
            }
            return 0;
        }
        private static int CompareFace(uint[] a, uint[] b)
        {
            int c = a.Length.CompareTo(b.Length); if (c != 0) return c;
            for (int i = 0; i < a.Length; i++) { c = a[i].CompareTo(b[i]); if (c != 0) return c; }
            return 0;
        }
        private static int CompareBytes(byte[] a, byte[] b)
        {
            int count = Math.Min(a.Length, b.Length);
            for (int i = 0; i < count; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
            return a.Length.CompareTo(b.Length);
        }
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static uint CheckedUInt32(int value) => checked((uint)value);
        private static ulong ReadUInt64(byte[] data, int offset)
        {
            uint low = ReadUInt32(data, offset); uint high = ReadUInt32(data, offset + 4);
            return low | ((ulong)high << 32);
        }
        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value); stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 24));
        }
        private static void WriteUInt64(Stream stream, ulong value)
        {
            WriteUInt32(stream, (uint)value); WriteUInt32(stream, (uint)(value >> 32));
        }
        private static void WriteSingle(Stream stream, float value) =>
            WriteUInt32(stream, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

        private readonly struct D3 { public D3(double x, double y, double z) { X = x; Y = y; Z = z; } public double X { get; } public double Y { get; } public double Z { get; } }
        private readonly struct Epsilon { public Epsilon(double d, double a, double v) { Distance = d; Area = a; Volume = v; } public double Distance { get; } public double Area { get; } public double Volume { get; } }
        private readonly struct Bounds { public Bounds(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) { MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ; } public double MinX { get; } public double MinY { get; } public double MinZ { get; } public double MaxX { get; } public double MaxY { get; } public double MaxZ { get; } }
        private readonly struct DirectedEdge { public DirectedEdge(uint start, uint end) { Start = start; End = end; } public uint Start { get; } public uint End { get; } }

        private sealed class Reader
        {
            private readonly byte[] data;
            public Reader(byte[] data, int offset) { this.data = data; Offset = offset; }
            public int Offset { get; private set; }
            public int Remaining => data.Length - Offset;
            public uint UInt32(string label)
            {
                if (Remaining < 4) throw new InvalidDataException("Truncated " + label + ".");
                uint value = ReadUInt32(data, Offset); Offset += 4; return value;
            }
            public float Single(string label)
            {
                uint bits = UInt32(label);
                float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidDataException(label + " is non-finite.");
                if (bits == 0x80000000U) throw new InvalidDataException(label + " is negative zero.");
                return value;
            }
        }
    }
}
