using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// TEST ONLY (2026-10-03): the cut input gate as it was before its two dictionaries took a hash of their own -- copied
    /// from VpCutInputGate at 498b67ca (the plain Check; the connectivity reuse left out), the default hash of long kept --
    /// so that the gate's verdict, rejection and element can be compared, input by input, with what it was.
    /// </summary>
    internal static class VpCutInputGateBefore
    {
        private struct EdgeUse
        {
            public int faces;
            public int firstDirection;
            public bool opposite;
        }


        public static VpCutInputVerdict Check(
            VpRenderVertex[] vertices,
            uint[] localIndices,
            int[] topologyOfVertex,
            int topologyVertexCount,
            VpGeometrySubmesh[] submeshes)
        {
            if (vertices == null || localIndices == null || topologyOfVertex == null || submeshes == null)
            {
                return new VpCutInputVerdict(VpCutInputRejection.MissingArray, -1);
            }

            int vertexCount = vertices.Length;
            int indexCount = localIndices.Length;

            // References: the same structural judgement the storage makes on any append, made here first so the gate
            // never reads outside an array.
            if (topologyVertexCount < 0
                || topologyOfVertex.Length != vertexCount
                || indexCount % 3 != 0
                || !VpCpuGeometryStorage.AreTopologyIdsInRange(topologyOfVertex, topologyVertexCount)
                || !VpCpuGeometryStorage.AreIndicesInRange(localIndices, 0, vertexCount)
                || !VpCpuGeometryStorage.DoSubmeshesCover(submeshes, 0, submeshes.Length, indexCount))
            {
                return new VpCutInputVerdict(VpCutInputRejection.InvalidReference, -1);
            }

            int triangleCount = indexCount / 3;

            // Every triangle names three different topology vertices.
            for (int t = 0; t < triangleCount; t++)
            {
                int a = topologyOfVertex[localIndices[3 * t]];
                int b = topologyOfVertex[localIndices[3 * t + 1]];
                int c = topologyOfVertex[localIndices[3 * t + 2]];
                if (a == b || b == c || c == a)
                {
                    return new VpCutInputVerdict(VpCutInputRejection.TriangleRepeatsTopologyVertex, t);
                }
            }

            // Finite attributes and one position per topology vertex, on the vertices the triangles use.
            var positionOf = new Vector3[topologyVertexCount];
            var positionSeen = new bool[topologyVertexCount];
            for (int i = 0; i < indexCount; i++)
            {
                int v = (int)localIndices[i];
                VpRenderVertex vertex = vertices[v];
                if (!IsFinite(vertex.position) || !IsFinite(vertex.normal) || !IsFinite(vertex.uv0))
                {
                    return new VpCutInputVerdict(VpCutInputRejection.NonFinite, v);
                }

                int topology = topologyOfVertex[v];
                if (!positionSeen[topology])
                {
                    positionSeen[topology] = true;
                    positionOf[topology] = vertex.position;
                }
                else if (!SamePosition(positionOf[topology], vertex.position))
                {
                    return new VpCutInputVerdict(VpCutInputRejection.PositionMismatch, topology);
                }
            }

            return CheckConnectivity(localIndices, topologyOfVertex, topologyVertexCount, triangleCount, indexCount);
        }

        // The edge and fan checks: what depends on the indices and the topology alone.
        private static VpCutInputVerdict CheckConnectivity(uint[] localIndices, int[] topologyOfVertex, int topologyVertexCount,
            int triangleCount, int indexCount)
        {
            // Every topology edge: exactly two faces, traversing it in opposite directions.
            var edges = new Dictionary<long, EdgeUse>(indexCount);
            for (int t = 0; t < triangleCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int from = topologyOfVertex[localIndices[3 * t + k]];
                    int to = topologyOfVertex[localIndices[3 * t + (k + 1) % 3]];
                    long key = EdgeKey(from, to);
                    int direction = from < to ? 1 : -1;
                    edges.TryGetValue(key, out EdgeUse use);
                    if (use.faces == 0)
                    {
                        use.firstDirection = direction;
                    }
                    else if (use.faces == 1)
                    {
                        use.opposite = direction != use.firstDirection;
                    }

                    use.faces++;
                    edges[key] = use;
                }
            }

            foreach (KeyValuePair<long, EdgeUse> edge in edges)
            {
                if (edge.Value.faces != 2)
                {
                    return new VpCutInputVerdict(VpCutInputRejection.EdgeFaceCount, (int)(edge.Key >> 32));
                }

                if (!edge.Value.opposite)
                {
                    return new VpCutInputVerdict(VpCutInputRejection.EdgeSameDirection, (int)(edge.Key >> 32));
                }
            }

            // Every topology vertex: its faces form one closed fan. With every edge shared by two opposite faces, each
            // face (v, b, c) around v contributes the link step b -> c, every link vertex starts and ends exactly one
            // step, and the steps form cycles. One fan is one cycle through all of them; a vertex two fans share has
            // two or more.
            var next = new Dictionary<long, int>(indexCount);
            var facesAround = new int[topologyVertexCount];
            var startOf = new int[topologyVertexCount];
            for (int v = 0; v < topologyVertexCount; v++)
            {
                startOf[v] = -1;
            }

            for (int t = 0; t < triangleCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = topologyOfVertex[localIndices[3 * t + k]];
                    int b = topologyOfVertex[localIndices[3 * t + (k + 1) % 3]];
                    int c = topologyOfVertex[localIndices[3 * t + (k + 2) % 3]];
                    long key = ((long)v << 32) | (uint)b;
                    if (next.ContainsKey(key))
                    {
                        // Two faces around v both leave v's link at b: already more than one fan at v.
                        return new VpCutInputVerdict(VpCutInputRejection.VertexMultipleFans, v);
                    }

                    next[key] = c;
                    facesAround[v]++;
                    if (startOf[v] < 0)
                    {
                        startOf[v] = b;
                    }
                }
            }

            for (int v = 0; v < topologyVertexCount; v++)
            {
                if (facesAround[v] == 0)
                {
                    continue;
                }

                int start = startOf[v];
                int current = start;
                int steps = 0;
                do
                {
                    if (!next.TryGetValue(((long)v << 32) | (uint)current, out int following))
                    {
                        return new VpCutInputVerdict(VpCutInputRejection.VertexMultipleFans, v);
                    }

                    current = following;
                    steps++;
                }
                while (current != start && steps <= facesAround[v]);

                if (current != start || steps != facesAround[v])
                {
                    return new VpCutInputVerdict(VpCutInputRejection.VertexMultipleFans, v);
                }
            }

            return new VpCutInputVerdict(VpCutInputRejection.None, -1);
        }

        private static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private static bool IsFinite(Vector2 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y);
        }

        private static bool SamePosition(Vector3 a, Vector3 b)
        {
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }
    }
}
