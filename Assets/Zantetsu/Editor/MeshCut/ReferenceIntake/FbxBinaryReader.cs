using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>One node of the FBX binary tree: name, typed properties, children.</summary>
    public sealed class FbxNode
    {
        public string Name;
        public readonly List<object> Properties = new List<object>();
        public readonly List<FbxNode> Children = new List<FbxNode>();

        public FbxNode Child(string name) { foreach (var c in Children) if (c.Name == name) return c; return null; }
        public IEnumerable<FbxNode> ChildrenNamed(string name) { foreach (var c in Children) if (c.Name == name) yield return c; }
        public T Property<T>(int i) => (T)Properties[i];
        public string PropertyString(int i) => Properties[i] as string;
        public long PropertyLong(int i) => Convert.ToInt64(Properties[i]);
        public double PropertyDouble(int i) => Convert.ToDouble(Properties[i]);

        /// <summary>A Properties70 entry by name: the node "P" whose first property is the name.</summary>
        public FbxNode Property70(string name)
        {
            var p70 = Child("Properties70");
            if (p70 == null) return null;
            foreach (var p in p70.ChildrenNamed("P")) if (p.Properties.Count > 0 && (p.Properties[0] as string) == name) return p;
            return null;
        }
    }

    /// <summary>
    /// Minimal reader of the Kaydara FBX binary container (versions 7.1 - 7.7, 32- and 64-bit records, zlib-compressed
    /// arrays). It exposes the node tree as data; nothing here interprets geometry. Used by the reference intake to read
    /// FBX control points and per-corner layers directly, so the topology that identifies seams is the file's own
    /// (control point index), never a positional weld.
    /// </summary>
    public static class FbxBinaryReader
    {
        static readonly byte[] k_magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0");

        public static FbxNode Read(string path, out uint version)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 27) throw new InvalidDataException("not an FBX binary file: too short");
            for (int i = 0; i < k_magic.Length; i++) if (bytes[i] != k_magic[i]) throw new InvalidDataException("not an FBX binary file: bad magic (ASCII FBX is not supported)");
            version = BitConverter.ToUInt32(bytes, 23);
            bool wide = version >= 7500;
            var root = new FbxNode { Name = "" };
            long pos = 27;
            while (pos < bytes.Length)
            {
                FbxNode node = ReadNode(bytes, ref pos, wide);
                if (node == null) break;
                root.Children.Add(node);
            }
            return root;
        }

        static FbxNode ReadNode(byte[] b, ref long pos, bool wide)
        {
            long endOffset, propertyCount, propertyListLength;
            if (wide)
            {
                endOffset = (long)BitConverter.ToUInt64(b, (int)pos); propertyCount = (long)BitConverter.ToUInt64(b, (int)pos + 8); propertyListLength = (long)BitConverter.ToUInt64(b, (int)pos + 16); pos += 24;
            }
            else
            {
                endOffset = BitConverter.ToUInt32(b, (int)pos); propertyCount = BitConverter.ToUInt32(b, (int)pos + 4); propertyListLength = BitConverter.ToUInt32(b, (int)pos + 8); pos += 12;
            }
            int nameLength = b[pos]; pos += 1;
            if (endOffset == 0) return null;   // null record
            var node = new FbxNode { Name = Encoding.ASCII.GetString(b, (int)pos, nameLength) };
            pos += nameLength;
            for (long i = 0; i < propertyCount; i++) node.Properties.Add(ReadProperty(b, ref pos));
            while (pos < endOffset)
            {
                FbxNode child = ReadNode(b, ref pos, wide);
                if (child == null) break;
                node.Children.Add(child);
            }
            pos = endOffset;
            return node;
        }

        static object ReadProperty(byte[] b, ref long pos)
        {
            char type = (char)b[pos]; pos += 1;
            switch (type)
            {
                case 'Y': { short v = BitConverter.ToInt16(b, (int)pos); pos += 2; return v; }
                case 'C': { bool v = b[pos] != 0; pos += 1; return v; }
                case 'I': { int v = BitConverter.ToInt32(b, (int)pos); pos += 4; return v; }
                case 'F': { float v = BitConverter.ToSingle(b, (int)pos); pos += 4; return v; }
                case 'D': { double v = BitConverter.ToDouble(b, (int)pos); pos += 8; return v; }
                case 'L': { long v = BitConverter.ToInt64(b, (int)pos); pos += 8; return v; }
                case 'S':
                case 'R':
                {
                    int len = BitConverter.ToInt32(b, (int)pos); pos += 4;
                    if (type == 'R') { var raw = new byte[len]; Array.Copy(b, pos, raw, 0, len); pos += len; return raw; }
                    string s = Encoding.UTF8.GetString(b, (int)pos, len); pos += len;
                    return s;
                }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int count = BitConverter.ToInt32(b, (int)pos);
                    int encoding = BitConverter.ToInt32(b, (int)pos + 4);
                    int compressedLength = BitConverter.ToInt32(b, (int)pos + 8);
                    pos += 12;
                    int elementSize = type == 'f' || type == 'i' ? 4 : type == 'b' ? 1 : 8;
                    byte[] data;
                    if (encoding == 0)
                    {
                        data = new byte[count * elementSize];
                        Array.Copy(b, pos, data, 0, data.Length);
                        pos += compressedLength;
                    }
                    else
                    {
                        // zlib: 2-byte header, deflate body, adler32 trailer
                        data = new byte[count * elementSize];
                        using (var ms = new MemoryStream(b, (int)pos + 2, compressedLength - 2))
                        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            int read = 0;
                            while (read < data.Length)
                            {
                                int n = ds.Read(data, read, data.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                            if (read != data.Length) throw new InvalidDataException("FBX array decompression yielded " + read + " of " + data.Length + " bytes");
                        }
                        pos += compressedLength;
                    }
                    switch (type)
                    {
                        case 'f': { var a = new float[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'i': { var a = new int[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        default: { var a = new bool[count]; for (int i = 0; i < count; i++) a[i] = data[i] != 0; return a; }
                    }
                }
                default:
                    throw new InvalidDataException("unknown FBX property type '" + type + "' at " + pos);
            }
        }
    }
}
