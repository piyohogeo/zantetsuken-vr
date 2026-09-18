using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>Why the cut input gate refused a geometry, or <see cref="None"/> when it accepted it.</summary>
    public enum VpCutInputRejection
    {
        /// <summary>Accepted.</summary>
        None = 0,

        /// <summary>An array was null.</summary>
        MissingArray,

        /// <summary>The topology map, the topology vertex count, the index count or the submesh descriptors do not
        /// describe the arrays: an invalid reference.</summary>
        InvalidReference,

        /// <summary>A triangle names the same topology vertex twice, so it has no three edges to check.</summary>
        TriangleRepeatsTopologyVertex,

        /// <summary>A position, normal or uv0 of a vertex a triangle uses is NaN or infinite.</summary>
        NonFinite,

        /// <summary>Two render vertices of one topology vertex carry different positions.</summary>
        PositionMismatch,

        /// <summary>A topology edge is used by a number of faces other than two: an open boundary (one, which is also
        /// how a T-junction appears on the topology) or a non-manifold edge (three or more).</summary>
        EdgeFaceCount,

        /// <summary>The two faces of a topology edge traverse it in the same direction: a local winding inconsistency.</summary>
        EdgeSameDirection,

        /// <summary>The faces around a topology vertex form more than one fan.</summary>
        VertexMultipleFans,
    }

    /// <summary>The gate's answer: why it refused, and the element it refused on (a vertex, triangle or topology id).</summary>
    public readonly struct VpCutInputVerdict
    {
        public readonly VpCutInputRejection rejection;

        /// <summary>The first element the rejection was found on, -1 when it names no single element.</summary>
        public readonly int element;

        public VpCutInputVerdict(VpCutInputRejection rejection, int element)
        {
            this.rejection = rejection;
            this.element = element;
        }

        public bool Accepted => rejection == VpCutInputRejection.None;

        public override string ToString()
        {
            return Accepted ? "accepted" : rejection + (element >= 0 ? " at " + element : string.Empty);
        }
    }

    /// <summary>
    /// The input contract of DESIGN 6.2 for a geometry that is to be cut, checked once, when it is registered as
    /// cuttable — never per frame, per draw, or on what a cut produces. It is a pure check of the arrays a caller would
    /// hand to <see cref="VpCpuGeometryStorage.TryAppendCuttable"/>: it writes nothing and keeps nothing.
    /// <para>
    /// **The topology is the caller's.** The per render vertex topology id (an FBX control point, for the intake that
    /// exists) is the only thing that says which render vertices are the same logical vertex. Nothing here welds by
    /// position, looks for coincident vertices, or treats two components that touch or overlap as one.
    /// </para>
    /// <para>
    /// **What is required.** Valid references (every index names a vertex, every topology id is in range, the submeshes
    /// cover the indices in whole triangles); finite position, normal and uv0 on every vertex a triangle uses; one
    /// position per topology vertex; every topology edge used by exactly two faces that traverse it in opposite
    /// directions; and the faces around every topology vertex forming one closed fan. The same judgement the Editor's
    /// verifier makes (<c>LogicalTopology.Validate</c> and the position check of <c>ReferenceCut</c>), written again here
    /// over plain arrays so that nothing at runtime depends on an Editor assembly.
    /// </para>
    /// <para>
    /// **What is deliberately not here** (DESIGN 6.2 and T-084): an outward test, signed volume, orientation
    /// normalisation, whole-mesh self-intersection, inside/outside, a winding bound, repair, automatic weld, or a
    /// replacement geometry. A fully reversed closed body, several closed components, self-intersecting or nested
    /// shells, and coincident components on separate topology are all accepted as they are, in their own orientation.
    /// A triangle of zero area is not a reason to refuse, and whether a side of a cut is empty is not this gate's
    /// question.
    /// </para>
    /// </summary>
    public static class VpCutInputGate
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

        /// <summary>
        /// The positions must be the one canonical value (DESIGN 6.2): compared value by value, not within a tolerance,
        /// because Vector3's == is approximate and a tolerance would be a weld by another name.
        /// </summary>
        private static bool SamePosition(Vector3 a, Vector3 b)
        {
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }
    }
}
