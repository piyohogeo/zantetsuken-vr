using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Test-only managed, binary64 polygon B-rep (probe Model/ConvexPoly): the representation of the reference lane,
    /// the verifier and the case generator. Faces are polygon loops with outward winding; edges are derived on demand.
    /// </summary>
    public sealed class ConvexPoly
    {
        public double3[] V;
        public int[][] F;

        public struct Edge
        {
            public int v0, v1; // v0 < v1
            public int f0, f1; // f0: face traversing v0->v1, f1: face traversing v1->v0 (-1 if missing)
        }

        Edge[] m_edges;

        public ConvexPoly() { }
        public ConvexPoly(double3[] v, int[][] f) { V = v; F = f; }

        public int VertexCount => V.Length;
        public int FaceCount => F.Length;
        public int FaceIndexCount { get { int n = 0; foreach (var f in F) n += f.Length; return n; } }
        public int EdgeCount => Edges.Length;

        public void InvalidateEdges() { m_edges = null; }

        /// Undirected edge table. Does not throw on non-manifold input; f1 or f0 stays -1, extra faces are ignored
        /// (the verifier reports those). Edge ordering is deterministic (first appearance).
        public Edge[] Edges
        {
            get
            {
                if (m_edges != null) return m_edges;
                var map = new Dictionary<long, int>();
                var list = new List<Edge>();
                for (int f = 0; f < F.Length; f++)
                {
                    var loop = F[f];
                    for (int k = 0; k < loop.Length; k++)
                    {
                        int a = loop[k], b = loop[(k + 1) % loop.Length];
                        int lo = math.min(a, b), hi = math.max(a, b);
                        long key = ((long)lo << 32) | (uint)hi;
                        if (!map.TryGetValue(key, out int ei))
                        {
                            ei = list.Count;
                            map[key] = ei;
                            list.Add(new Edge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                        }
                        var e = list[ei];
                        if (a == lo) { if (e.f0 < 0) e.f0 = f; }
                        else { if (e.f1 < 0) e.f1 = f; }
                        list[ei] = e;
                    }
                }
                m_edges = list.ToArray();
                return m_edges;
            }
        }

        public double3 FaceNormalUnnormalized(int f)
        {
            var loop = F[f];
            double3 n = 0;
            for (int k = 0; k < loop.Length; k++)
            {
                double3 a = V[loop[k]], b = V[loop[(k + 1) % loop.Length]];
                n.x += (a.y - b.y) * (a.z + b.z);
                n.y += (a.z - b.z) * (a.x + b.x);
                n.z += (a.x - b.x) * (a.y + b.y);
            }
            return n;
        }

        public double3 FaceCentroid(int f)
        {
            var loop = F[f];
            double3 c = 0;
            foreach (var i in loop) c += V[i];
            return c / loop.Length;
        }

        /// Unit normal n and offset d such that dot(n, x) - d = 0 on the face plane (d uses the face centroid).
        public void FacePlane(int f, out double3 n, out double d)
        {
            n = math.normalize(FaceNormalUnnormalized(f));
            d = math.dot(n, FaceCentroid(f));
        }

        public double3 VertexCentroid()
        {
            double3 c = 0;
            foreach (var v in V) c += v;
            return c / V.Length;
        }

        public void Bounds(out double3 min, out double3 max)
        {
            min = new double3(double.MaxValue); max = new double3(double.MinValue);
            foreach (var v in V) { min = math.min(min, v); max = math.max(max, v); }
        }

        public double MaxExtent()
        {
            Bounds(out var mn, out var mx);
            return math.cmax(mx - mn);
        }

        public ConvexPoly Clone()
        {
            var f = new int[F.Length][];
            for (int i = 0; i < F.Length; i++) f[i] = (int[])F[i].Clone();
            return new ConvexPoly((double3[])V.Clone(), f);
        }

        public ConvexPoly Transformed(double3 scale, double3 offset)
        {
            var c = Clone();
            for (int i = 0; i < c.V.Length; i++) c.V[i] = c.V[i] * scale + offset;
            return c;
        }

        public ConvexPoly Rotated(quaternion q)
        {
            // apply one double rotation matrix (from the float quaternion) so planarity is preserved to double rounding
            var R = (double3x3)new float3x3(q);
            var c = Clone();
            for (int i = 0; i < c.V.Length; i++) c.V[i] = math.mul(R, c.V[i]);
            return c;
        }

        /// Rounds every vertex to float (what the kernels see).
        public ConvexPoly RoundedToFloat()
        {
            var c = Clone();
            for (int i = 0; i < c.V.Length; i++) c.V[i] = (float3)c.V[i];
            return c;
        }
    }

    /// <summary>Managed flat copy of a float B-rep in the kernel layout (polygon CSR + undirected edges + faceEdge).</summary>
    public sealed class ConvexBrepData
    {
        public float3[] v;
        public int[] faceOff;
        public int[] faceIdx;
        public int[] faceEdge;
        public BrepEdge[] edges;
        public int V => v.Length;
        public int F => faceOff.Length - 1;
        public int I => faceIdx.Length;
        public int E => edges.Length;
        public int Lmax { get { int m = 0; for (int f = 0; f < F; f++) m = Math.Max(m, faceOff[f + 1] - faceOff[f]); return m; } }

        public static ConvexBrepData FromPoly(ConvexPoly p)
        {
            var d = new ConvexBrepData();
            d.v = new float3[p.V.Length];
            for (int i = 0; i < d.v.Length; i++) d.v[i] = (float3)p.V[i];
            d.faceOff = new int[p.F.Length + 1];
            var idx = new List<int>();
            for (int f = 0; f < p.F.Length; f++) { d.faceOff[f] = idx.Count; idx.AddRange(p.F[f]); }
            d.faceOff[p.F.Length] = idx.Count;
            d.faceIdx = idx.ToArray();
            var pe = p.Edges;
            d.edges = new BrepEdge[pe.Length];
            var map = new Dictionary<long, int>();
            for (int e = 0; e < pe.Length; e++)
            {
                d.edges[e] = new BrepEdge { v0 = pe[e].v0, v1 = pe[e].v1, f0 = pe[e].f0, f1 = pe[e].f1 };
                map[((long)pe[e].v0 << 32) | (uint)pe[e].v1] = e;
            }
            d.faceEdge = new int[d.faceIdx.Length];
            for (int f = 0; f < p.F.Length; f++)
            {
                var loop = p.F[f];
                for (int k = 0; k < loop.Length; k++)
                {
                    int a = loop[k], b = loop[(k + 1) % loop.Length];
                    d.faceEdge[d.faceOff[f] + k] = map[((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b)];
                }
            }
            return d;
        }

        public ConvexPoly ToPoly()
        {
            var vv = new double3[V];
            for (int i = 0; i < V; i++) vv[i] = v[i];
            var ff = new int[F][];
            for (int f = 0; f < F; f++)
            {
                int n = faceOff[f + 1] - faceOff[f];
                ff[f] = new int[n];
                Array.Copy(faceIdx, faceOff[f], ff[f], 0, n);
            }
            return new ConvexPoly(vv, ff);
        }

        public static unsafe ConvexBrepData FromBuffer(in BrepBuffer b)
        {
            var d = new ConvexBrepData
            {
                v = new float3[b.V], faceOff = new int[b.F + 1], faceIdx = new int[b.I], faceEdge = new int[b.I], edges = new BrepEdge[b.E],
            };
            for (int i = 0; i < b.V; i++) d.v[i] = b.v[i];
            for (int i = 0; i <= b.F; i++) d.faceOff[i] = b.faceOff[i];
            for (int i = 0; i < b.I; i++) { d.faceIdx[i] = b.faceIdx[i]; d.faceEdge[i] = b.faceEdge != null ? b.faceEdge[i] : -1; }
            for (int i = 0; i < b.E; i++) d.edges[i] = b.edges[i];
            return d;
        }

        public static unsafe ConvexBrepData FromBank(in ConvexBrepBank bank, in ConvexBrepRange r)
        {
            var view = bank.View(in r);
            return FromBuffer(in view);
        }

        /// Copies into native arrays owned by the caller and returns a view (capacities = the array lengths).
        public unsafe BrepBuffer Upload(NativeArray<float3> nv, NativeArray<int> nfo, NativeArray<int> nfi, NativeArray<int> nfe, NativeArray<BrepEdge> ne)
        {
            nv.GetSubArray(0, V).CopyFrom(v);
            nfo.GetSubArray(0, F + 1).CopyFrom(faceOff);
            nfi.GetSubArray(0, I).CopyFrom(faceIdx);
            nfe.GetSubArray(0, I).CopyFrom(faceEdge);
            ne.GetSubArray(0, E).CopyFrom(edges);
            return new BrepBuffer
            {
                v = (float3*)nv.GetUnsafePtr(), faceOff = (int*)nfo.GetUnsafePtr(), faceIdx = (int*)nfi.GetUnsafePtr(),
                faceEdge = (int*)nfe.GetUnsafePtr(), edges = (BrepEdge*)ne.GetUnsafePtr(),
                V = V, F = F, I = I, E = E, vCap = nv.Length, fCap = nfo.Length - 1, iCap = nfi.Length, eCap = ne.Length,
            };
        }

        public ConvexBrepRange RangeAt(int vertexBase, int faceBase, int faceIndexBase, int edgeBase) => new ConvexBrepRange
        {
            vertexBase = vertexBase, vertexCount = V, faceBase = faceBase, faceCount = F, faceIndexBase = faceIndexBase, faceIndexCount = I,
            edgeBase = edgeBase, edgeCount = E, maxFaceLoop = Lmax,
        };
    }
}
