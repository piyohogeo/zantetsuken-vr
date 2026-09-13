using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>A generated geometry in local numbering: render vertices, indices (3 per triangle, grouped by submesh) and the topology id of each render vertex.</summary>
    public sealed class SyntheticMesh
    {
        public RenderVertex[] Vertices;
        public uint[] Indices;
        public int[] TopologyOfVertex;
        public int TopologyVertexCount;
        public readonly List<int> SubmeshIndexCounts = new List<int>();
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Public synthetic inputs of the contract tests (DESIGN 10.5 "Synthetic Watertight Test Fixture"): closed,
    /// consistently oriented logical meshes with known properties, a representative game vertex, and optional attribute
    /// seams built the way an asset pipeline would (corner attributes deduplicated per logical vertex). Identity is
    /// always topological: coincident vertices of different components stay distinct, welded lattice corners of one
    /// component are one topology vertex.
    /// </summary>
    public sealed class LogicalMeshBuilder
    {
        public readonly List<float3> Positions = new List<float3>();
        public readonly List<int> Triangles = new List<int>();
        public readonly List<int> Submesh = new List<int>();
        public int CurrentSubmesh;

        public int AddVertex(float3 p) { Positions.Add(p); return Positions.Count - 1; }
        public int AddVertex(float x, float y, float z) => AddVertex(new float3(x, y, z));
        public void AddTriangle(int a, int b, int c) { Triangles.Add(a); Triangles.Add(b); Triangles.Add(c); Submesh.Add(CurrentSubmesh); }
        public void AddQuad(int a, int b, int c, int d) { AddTriangle(a, b, c); AddTriangle(a, c, d); }
        public int VertexCount => Positions.Count;
        public int TriangleCount => Triangles.Count / 3;

        public void Reverse()
        {
            for (int i = 0; i < Triangles.Count; i += 3) { int t = Triangles[i + 1]; Triangles[i + 1] = Triangles[i + 2]; Triangles[i + 2] = t; }
        }

        /// <summary>Appends another builder as a separate component (fresh logical vertices).</summary>
        public void Append(LogicalMeshBuilder other, int submesh = -1)
        {
            int baseV = Positions.Count;
            Positions.AddRange(other.Positions);
            for (int i = 0; i < other.Triangles.Count; i++) Triangles.Add(other.Triangles[i] + baseV);
            for (int i = 0; i < other.Submesh.Count; i++) Submesh.Add(submesh >= 0 ? submesh : other.Submesh[i]);
        }

        public void Translate(float3 d) { for (int i = 0; i < Positions.Count; i++) Positions[i] += d; }

        // ---------------------------------------------------------------- attributes and render layer

        public sealed class AttributeOptions
        {
            /// <summary>Degrees; faces meeting at a sharper angle get split render vertices (180 = all smooth, no seams).</summary>
            public double CreaseAngle = 180;
            /// <summary>One cylindrical UV seam around Y instead of a seamless planar map.</summary>
            public bool CylindricalUv;
            /// <summary>Shift applied to every UV (negative values exercise the negative-UV tolerance of DESIGN 5.3).</summary>
            public float2 UvOffset;
            public bool ForceSeamLayer => CreaseAngle < 180 || CylindricalUv;
        }

        /// <summary>
        /// Builds the render vertices. Without seams every logical vertex is one render vertex with a smooth normal.
        /// With seams, corner attributes (crease-limited smoothed normal, planar or cylindrical UV, a perpendicular
        /// tangent) are deduplicated per logical vertex by exact equality into render vertices; the corner -> render
        /// mapping becomes the index buffer and the render -> logical mapping the topology map.
        /// </summary>
        public SyntheticMesh Finish(AttributeOptions o = null)
        {
            o = o ?? new AttributeOptions();
            int V = Positions.Count, T = Triangles.Count / 3;
            var faceNormal = new float3[T];
            var faceArea = new float[T];
            var vertexFaces = new List<int>[V];
            for (int v = 0; v < V; v++) vertexFaces[v] = new List<int>();
            for (int t = 0; t < T; t++)
            {
                int a = Triangles[3 * t], b = Triangles[3 * t + 1], c = Triangles[3 * t + 2];
                float3 n = math.cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                float len = math.length(n);
                faceArea[t] = len;
                faceNormal[t] = len > 0 ? n / len : float3.zero;
                vertexFaces[a].Add(t); vertexFaces[b].Add(t); vertexFaces[c].Add(t);
            }
            float3 mn = new float3(float.MaxValue), mx = new float3(float.MinValue);
            foreach (var p in Positions) { mn = math.min(mn, p); mx = math.max(mx, p); }
            float3 size = mx - mn, center = 0.5f * (mn + mx);
            float invX = size.x > 1e-20f ? 1f / size.x : 0f, invY = size.y > 1e-20f ? 1f / size.y : 0f, invZ = size.z > 1e-20f ? 1f / size.z : 0f;
            float cosCrease = (float)Math.Cos(o.CreaseAngle * Math.PI / 180.0);

            // corner attributes
            var cornerN = new float3[3 * T];
            var cornerUv = new float2[3 * T];
            for (int t = 0; t < T; t++)
            {
                float triAngle = 0f;
                if (o.CylindricalUv)
                {
                    double sx = 0, sz = 0;
                    for (int i = 0; i < 3; i++) { float3 p = Positions[Triangles[3 * t + i]]; double ang = Math.Atan2(p.z - center.z, p.x - center.x); sx += Math.Cos(ang); sz += Math.Sin(ang); }
                    triAngle = (float)Math.Atan2(sz, sx);
                }
                for (int i = 0; i < 3; i++)
                {
                    int v = Triangles[3 * t + i];
                    float3 sum = float3.zero;
                    foreach (int f in vertexFaces[v])
                    {
                        if (faceArea[f] <= 0f) continue;
                        if (f != t && math.dot(faceNormal[f], faceNormal[t]) < cosCrease) continue;
                        sum += faceNormal[f] * faceArea[f];
                    }
                    float3 nn = math.lengthsq(sum) > 1e-20f ? math.normalize(sum) : (math.lengthsq(faceNormal[t]) > 0 ? faceNormal[t] : new float3(0, 1, 0));
                    cornerN[3 * t + i] = nn;
                    float3 p = Positions[v];
                    if (!o.CylindricalUv) cornerUv[3 * t + i] = new float2((p.x - mn.x) * invX, (p.z - mn.z) * invZ) + o.UvOffset;
                    else
                    {
                        double ang = Math.Atan2(p.z - center.z, p.x - center.x);
                        double d = ang - triAngle;
                        while (d > Math.PI) d -= 2 * Math.PI;
                        while (d < -Math.PI) d += 2 * Math.PI;
                        cornerUv[3 * t + i] = new float2((float)((triAngle + d) / (2 * Math.PI) + 0.5), (p.y - mn.y) * invY) + o.UvOffset;
                    }
                }
            }

            var mesh = new SyntheticMesh();
            var renderLogical = new List<int>();
            var renderN = new List<float3>();
            var renderUv = new List<float2>();
            var cornerRender = new uint[3 * T];
            if (!o.ForceSeamLayer)
            {
                // one render vertex per logical vertex: area-weighted smooth normal, the first corner's UV
                var firstUv = new float2[V];
                var seen = new bool[V];
                for (int t = 0; t < T; t++)
                    for (int i = 0; i < 3; i++) { int v = Triangles[3 * t + i]; if (!seen[v]) { seen[v] = true; firstUv[v] = cornerUv[3 * t + i]; } }
                for (int v = 0; v < V; v++)
                {
                    float3 sum = float3.zero;
                    foreach (int f in vertexFaces[v]) sum += faceNormal[f] * faceArea[f];
                    renderLogical.Add(v);
                    renderN.Add(math.lengthsq(sum) > 1e-20f ? math.normalize(sum) : new float3(0, 1, 0));
                    renderUv.Add(firstUv[v]);
                }
                for (int i = 0; i < 3 * T; i++) cornerRender[i] = (uint)Triangles[i];
            }
            else
            {
                var firstOf = new int[V];
                var nextOf = new List<int>();
                for (int v = 0; v < V; v++) firstOf[v] = -1;
                for (int i = 0; i < 3 * T; i++)
                {
                    int v = Triangles[i];
                    int r = firstOf[v], found = -1;
                    while (r >= 0)
                    {
                        if (renderN[r].Equals(cornerN[i]) && renderUv[r].Equals(cornerUv[i])) { found = r; break; }
                        r = nextOf[r];
                    }
                    if (found < 0)
                    {
                        found = renderLogical.Count;
                        renderLogical.Add(v); renderN.Add(cornerN[i]); renderUv.Add(cornerUv[i]);
                        nextOf.Add(firstOf[v]);
                        firstOf[v] = found;
                    }
                    cornerRender[i] = (uint)found;
                }
            }

            int R = renderLogical.Count;
            mesh.Vertices = new RenderVertex[R];
            mesh.TopologyOfVertex = new int[R];
            for (int r = 0; r < R; r++)
            {
                float3 n = renderN[r];
                float3 reference = math.abs(n.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
                float3 tangent = math.cross(reference, n);
                tangent = math.lengthsq(tangent) > 1e-20f ? math.normalize(tangent) : new float3(1, 0, 0);
                mesh.Vertices[r] = new RenderVertex { position = Positions[renderLogical[r]], normal = n, uv0 = renderUv[r], tangent = new float4(tangent, 1f) };
                mesh.TopologyOfVertex[r] = renderLogical[r];
            }
            mesh.TopologyVertexCount = V;

            // indices grouped by submesh in submesh order
            int submeshCount = 0;
            foreach (int s in Submesh) submeshCount = Math.Max(submeshCount, s + 1);
            var indices = new List<uint>(3 * T);
            for (int s = 0; s < submeshCount; s++)
            {
                int before = indices.Count;
                for (int t = 0; t < T; t++)
                {
                    if (Submesh[t] != s) continue;
                    indices.Add(cornerRender[3 * t]); indices.Add(cornerRender[3 * t + 1]); indices.Add(cornerRender[3 * t + 2]);
                }
                mesh.SubmeshIndexCounts.Add(indices.Count - before);
            }
            mesh.Indices = indices.ToArray();
            return mesh;
        }
    }

    public static class SyntheticGeometry
    {
        // axis, positive side, then the two tangent axes ordered so cross(t0, t1) points outward.
        static readonly int[,] k_boxFaces = { { 0, 1, 1, 2 }, { 0, 0, 2, 1 }, { 1, 1, 2, 0 }, { 1, 0, 0, 2 }, { 2, 1, 0, 1 }, { 2, 0, 1, 0 } };

        /// <summary>Closed box subdivided n times per axis; lattice corners are welded by integer identity, not by position. Optionally two submeshes (faces 0-2 / 3-5).</summary>
        public static LogicalMeshBuilder Box(int n, float3 size, float3 offset, bool twoSubmeshes = false)
        {
            var b = new LogicalMeshBuilder();
            AppendBox(b, n, size, offset, twoSubmeshes);
            return b;
        }

        public static void AppendBox(LogicalMeshBuilder b, int n, float3 size, float3 offset, bool twoSubmeshes = false)
        {
            n = Math.Max(1, n);
            var lattice = new Dictionary<int, int>();
            int stride = n + 1;
            var c = new int[3];
            int Vertex(int i, int j, int k)
            {
                int key = (i * stride + j) * stride + k;
                if (lattice.TryGetValue(key, out int id)) return id;
                id = b.AddVertex(((float)i / n - 0.5f) * size.x + offset.x, ((float)j / n - 0.5f) * size.y + offset.y, ((float)k / n - 0.5f) * size.z + offset.z);
                lattice.Add(key, id);
                return id;
            }
            int Corner(int axis, int positive, int t0, int t1, int u, int v)
            {
                c[axis] = positive == 1 ? n : 0; c[t0] = u; c[t1] = v;
                return Vertex(c[0], c[1], c[2]);
            }
            for (int f = 0; f < 6; f++)
            {
                b.CurrentSubmesh = twoSubmeshes && f >= 3 ? 1 : b.CurrentSubmesh;
                int axis = k_boxFaces[f, 0], positive = k_boxFaces[f, 1], t0 = k_boxFaces[f, 2], t1 = k_boxFaces[f, 3];
                for (int u = 0; u < n; u++)
                    for (int v = 0; v < n; v++)
                        b.AddQuad(Corner(axis, positive, t0, t1, u, v), Corner(axis, positive, t0, t1, u + 1, v), Corner(axis, positive, t0, t1, u + 1, v + 1), Corner(axis, positive, t0, t1, u, v + 1));
            }
            b.CurrentSubmesh = 0;
        }

        public static LogicalMeshBuilder UvSphere(int segments, int rings, float radius, float3 offset)
        {
            segments = Math.Max(3, segments); rings = Math.Max(2, rings);
            var b = new LogicalMeshBuilder();
            int north = b.AddVertex(offset + new float3(0, radius, 0));
            var rowStart = new int[rings];
            for (int r = 1; r < rings; r++)
            {
                double phi = Math.PI * r / rings;
                float y = (float)(radius * Math.Cos(phi)), rad = (float)(radius * Math.Sin(phi));
                rowStart[r] = b.VertexCount;
                for (int s = 0; s < segments; s++)
                {
                    double th = 2.0 * Math.PI * s / segments;
                    b.AddVertex(offset + new float3((float)(rad * Math.Cos(th)), y, (float)(rad * Math.Sin(th))));
                }
            }
            int south = b.AddVertex(offset + new float3(0, -radius, 0));
            for (int s = 0; s < segments; s++)
            {
                int s1 = (s + 1) % segments;
                b.AddTriangle(north, rowStart[1] + s1, rowStart[1] + s);
                b.AddTriangle(south, rowStart[rings - 1] + s, rowStart[rings - 1] + s1);
            }
            for (int r = 1; r < rings - 1; r++)
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    b.AddQuad(rowStart[r] + s, rowStart[r] + s1, rowStart[r + 1] + s1, rowStart[r + 1] + s);
                }
            return b;
        }

        public static LogicalMeshBuilder Torus(int major, int minor, float R, float r, float3 offset)
        {
            major = Math.Max(3, major); minor = Math.Max(3, minor);
            var b = new LogicalMeshBuilder();
            for (int i = 0; i < major; i++)
            {
                double u = 2.0 * Math.PI * i / major, cu = Math.Cos(u), su = Math.Sin(u);
                for (int j = 0; j < minor; j++)
                {
                    double v = 2.0 * Math.PI * j / minor, rad = R + r * Math.Cos(v);
                    b.AddVertex(offset + new float3((float)(rad * cu), (float)(r * Math.Sin(v)), (float)(rad * su)));
                }
            }
            for (int i = 0; i < major; i++)
                for (int j = 0; j < minor; j++)
                {
                    int i1 = (i + 1) % major, j1 = (j + 1) % minor;
                    b.AddQuad(i * minor + j, i1 * minor + j, i1 * minor + j1, i * minor + j1);
                }
            return b;
        }

        public static LogicalMeshBuilder Tetrahedron(float s)
        {
            var b = new LogicalMeshBuilder();
            int a = b.AddVertex(s, s, s), c = b.AddVertex(s, -s, -s), d = b.AddVertex(-s, s, -s), e = b.AddVertex(-s, -s, s);
            b.AddTriangle(a, c, d); b.AddTriangle(a, d, e); b.AddTriangle(a, e, c); b.AddTriangle(c, e, d);
            return b;
        }

        public static LogicalMeshBuilder Octahedron(float s)
        {
            var b = new LogicalMeshBuilder();
            int px = b.AddVertex(s, 0, 0), nx = b.AddVertex(-s, 0, 0), py = b.AddVertex(0, s, 0), ny = b.AddVertex(0, -s, 0), pz = b.AddVertex(0, 0, s), nz = b.AddVertex(0, 0, -s);
            b.AddTriangle(px, py, pz); b.AddTriangle(py, nx, pz); b.AddTriangle(nx, ny, pz); b.AddTriangle(ny, px, pz);
            b.AddTriangle(py, px, nz); b.AddTriangle(nx, py, nz); b.AddTriangle(ny, nx, nz); b.AddTriangle(px, ny, nz);
            return b;
        }

        /// <summary>Closed extrusion of a 2D section along Z with centre fans; valid combinatorially for any section (a self-crossing section gives a self-intersecting closed surface, allowed by DESIGN 6.2).</summary>
        static LogicalMeshBuilder ExtrudeSection(float2[] section, int axial, float height, float3 offset)
        {
            axial = Math.Max(1, axial);
            int m = section.Length;
            var b = new LogicalMeshBuilder();
            for (int a = 0; a <= axial; a++)
            {
                float z = ((float)a / axial - 0.5f) * height;
                for (int i = 0; i < m; i++) b.AddVertex(offset + new float3(section[i].x, section[i].y, z));
            }
            for (int a = 0; a < axial; a++)
                for (int i = 0; i < m; i++)
                {
                    int i1 = (i + 1) % m;
                    b.AddQuad(a * m + i, a * m + i1, (a + 1) * m + i1, (a + 1) * m + i);
                }
            int lastRow = axial * m;
            int cLo = b.AddVertex(offset + new float3(0, 0, -height * 0.5f)), cHi = b.AddVertex(offset + new float3(0, 0, height * 0.5f));
            for (int i = 0; i < m; i++)
            {
                int i1 = (i + 1) % m;
                b.AddTriangle(cLo, i1, i);
                b.AddTriangle(cHi, lastRow + i, lastRow + i1);
            }
            return b;
        }

        /// <summary>Concave but simple cut loop: a star cross-section prism.</summary>
        public static LogicalMeshBuilder StarPrism(int points, float outer, float inner, float height, int axial, float3 offset)
        {
            points = Math.Max(3, points);
            var section = new float2[points * 2];
            for (int i = 0; i < points * 2; i++)
            {
                double th = Math.PI * i / points;
                float rad = i % 2 == 0 ? outer : inner;
                section[i] = new float2((float)(rad * Math.Cos(th)), (float)(rad * Math.Sin(th)));
            }
            return ExtrudeSection(section, axial, height, offset);
        }

        /// <summary>Closed self-intersecting surface: a Gerono lemniscate section extruded and closed with centre fans (the centre is the crossing point).</summary>
        public static LogicalMeshBuilder LemniscateTube(int segments, int axial, float a, float height, float3 offset)
        {
            segments = Math.Max(8, segments);
            if (segments % 4 == 2) segments++;
            var section = new float2[segments];
            for (int i = 0; i < segments; i++)
            {
                double t = 2.0 * Math.PI * (i + 0.5) / segments;
                section[i] = new float2((float)(a * Math.Cos(t)), (float)(a * Math.Sin(t) * Math.Cos(t)));
            }
            return ExtrudeSection(section, axial, height, offset);
        }

        /// <summary>
        /// Closed prism whose section touches itself: a notch from the top edge reaches down to the bottom edge exactly, so
        /// a cut across the prism yields one loop with a node lying exactly on a non-adjacent segment (a self-touching
        /// contour, the split cap's contact path) and the notch tip is a cut port shared by two coincident positions.
        /// </summary>
        public static LogicalMeshBuilder TouchingPrism(float height, int axial, float3 offset)
        {
            var section = new[]
            {
                new float2(-1f, -1f), new float2(1f, -1f), new float2(1f, 1f), new float2(0.2f, 1f), new float2(0f, -1f), new float2(-0.2f, 1f), new float2(-1f, 1f),
            };
            return ExtrudeSection(section, axial, height, offset);
        }

        /// <summary>Two triangles over the same three logical vertices with opposite winding: manifold by the 6.2 rules, zero volume.</summary>
        public static LogicalMeshBuilder OppositeCoincidentPair(float s, float3 offset)
        {
            var b = new LogicalMeshBuilder();
            int a = b.AddVertex(offset + new float3(-s, 0, -s)), c = b.AddVertex(offset + new float3(s, 0, -s)), d = b.AddVertex(offset + new float3(0, 0, s));
            b.AddTriangle(a, c, d); b.AddTriangle(a, d, c);
            return b;
        }

        /// <summary>Box (n = 2) whose +Y face centre vertex is moved onto the face's edge line, making two of its triangles zero-area while the topology stays closed and manifold.</summary>
        public static LogicalMeshBuilder BoxWithZeroAreaTriangles(float3 size, float3 offset)
        {
            var b = Box(2, size, offset);
            // the +Y face centre is at (offset.x, offset.y + size.y/2, offset.z); move it onto the x = +size/2 edge line of that face
            float3 target = offset + new float3(size.x * 0.5f, size.y * 0.5f, 0f);
            for (int v = 0; v < b.Positions.Count; v++)
                if (math.all(b.Positions[v] == offset + new float3(0, size.y * 0.5f, 0))) { b.Positions[v] = target; break; }
            return b;
        }

        /// <summary>A sphere with a reversed smaller sphere inside: an internal shell (DESIGN 6.2 allows Internal / Nested Shell).</summary>
        public static LogicalMeshBuilder NestedShells(float3 offset)
        {
            var outer = UvSphere(24, 12, 1f, offset);
            var inner = UvSphere(16, 8, 0.5f, offset);
            inner.Reverse();
            outer.Append(inner);
            return outer;
        }

        /// <summary>Disjoint boxes in a row: one plane produces several independent loops, and a plane between two boxes splits the components with K = 0.</summary>
        public static LogicalMeshBuilder BarField(int count, int n, float size, float pitch, float3 offset)
        {
            var b = new LogicalMeshBuilder();
            for (int c = 0; c < count; c++) AppendBox(b, n, new float3(size, size * 3f, size), offset + new float3((c - (count - 1) * 0.5f) * pitch, 0, 0));
            return b;
        }

        public static float4 Plane(float3 normal, float3 point)
        {
            float3 n = math.normalize(normal);
            return new float4(n, -math.dot(n, point));
        }
    }
}
