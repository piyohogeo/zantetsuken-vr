using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>One block of the managed topology map: render vertices [vertexBase, vertexBase + topologyVertex.Length) and their topology ids.</summary>
    public sealed class TopologyBlock
    {
        public uint VertexBase;
        public int[] TopologyVertex;
        public int Count => TopologyVertex.Length;
        public bool Contains(uint r) => r >= VertexBase && r < VertexBase + (uint)TopologyVertex.Length;
        public int Of(uint r) => TopologyVertex[r - VertexBase];
    }

    /// <summary>
    /// Managed (test-side) view of one geometry inside a global vertex / index pool: the pool arrays it addresses, its
    /// submesh index ranges and its topology map. Existing vertices keep their global numbers across cuts, so a child
    /// geometry shares the pool arrays with its parent and adds the block the cut appended.
    /// </summary>
    /// <summary>The managed pool arrays a family of geometries addresses; grown in place by the harness as cuts append blocks.</summary>
    public sealed class PoolArrays
    {
        public RenderVertex[] Vertices = Array.Empty<RenderVertex>();
        public uint[] Indices = Array.Empty<uint>();
    }

    public sealed class CutGeometry
    {
        public string Name;
        public PoolArrays Pool;
        public RenderVertex[] Vertices => Pool.Vertices;
        public uint[] Indices => Pool.Indices;
        public readonly List<MeshCutIndexRange> Ranges = new List<MeshCutIndexRange>();
        public readonly List<TopologyBlock> Topology = new List<TopologyBlock>();
        public int TopologyVertexCount;

        public int TriangleCount
        {
            get { int t = 0; foreach (var r in Ranges) t += r.indexCount / 3; return t; }
        }

        public int TopologyOf(uint r)
        {
            foreach (var b in Topology) if (b.Contains(r)) return b.Of(r);
            return -1;
        }

        /// <summary>Global render indices of every triangle, in range order.</summary>
        public IEnumerable<(uint a, uint b, uint c, int range)> Triangles()
        {
            for (int r = 0; r < Ranges.Count; r++)
            {
                var range = Ranges[r];
                for (int i = 0; i < range.indexCount; i += 3)
                    yield return (Indices[range.indexStart + i], Indices[range.indexStart + i + 1], Indices[range.indexStart + i + 2], r);
            }
        }

        public HashSet<uint> ReferencedVertices()
        {
            var set = new HashSet<uint>();
            foreach (var (a, b, c, _) in Triangles()) { set.Add(a); set.Add(b); set.Add(c); }
            return set;
        }

        public (double3 min, double3 max) Bounds()
        {
            double3 mn = new double3(double.MaxValue), mx = new double3(double.MinValue);
            foreach (uint v in ReferencedVertices()) { double3 p = Vertices[v].position; mn = math.min(mn, p); mx = math.max(mx, p); }
            return (mn, mx);
        }

        public double MaxExtent()
        {
            var (mn, mx) = Bounds();
            return math.cmax(mx - mn);
        }

        public double TotalArea()
        {
            double sum = 0.0;
            foreach (var (a, b, c, _) in Triangles())
            {
                double3 pa = Vertices[a].position, pb = Vertices[b].position, pc = Vertices[c].position;
                sum += 0.5 * math.length(math.cross(pb - pa, pc - pa));
            }
            return sum;
        }

        /// <summary>A new geometry over the same pool arrays with the given ranges and the parent's topology plus one appended block.</summary>
        public CutGeometry Child(string name, IEnumerable<MeshCutIndexRange> ranges, TopologyBlock appended, int topologyVertexCount)
        {
            var g = new CutGeometry { Name = name, Pool = Pool, TopologyVertexCount = topologyVertexCount };
            foreach (var r in ranges) if (r.indexCount > 0) g.Ranges.Add(r);
            g.Topology.AddRange(Topology);
            if (appended != null && appended.Count > 0) g.Topology.Add(appended);
            return g;
        }
    }
}
