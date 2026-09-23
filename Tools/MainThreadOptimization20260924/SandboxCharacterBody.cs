using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The measurement's representative input: a character the user provided with its convex hulls -- the intake
    /// export of one <c>-convex</c> blend, read from two private JSON files (the hulls moved to the rig's rest pose,
    /// and the render meshes as the editor's FBX reader gives them). Both are in the same frame, Blender's right-handed
    /// Z-up metres, so nothing is converted: the body's own local frame is that frame, and a cut plane for it is given
    /// in it. The physics shape is authored the way the sandbox authors its boxes (one B-rep bank, one cooked collider
    /// mesh per hull, an external shape source); the render meshes become one cuttable geometry with one submesh per
    /// mesh and their own topology. Sandbox only: nothing of the product changes for it.
    /// </summary>
    public sealed unsafe class SandboxCharacterBody : IDisposable
    {
        private readonly List<IDisposable> _native = new List<IDisposable>(8);
        private readonly List<Mesh> _meshes = new List<Mesh>(24);
        private bool _disposed;

        public PhysicsOwnerShape Shape { get; private set; }

        public VpStoredGeometry Geometry { get; private set; }

        public int HullCount { get; private set; }

        public int HullVertexCount { get; private set; }

        public int HullFaceCount { get; private set; }

        public int RenderVertexCount { get; private set; }

        public int RenderIndexCount { get; private set; }

        public int SubmeshCount { get; private set; }

        public float3 BoundsMin { get; private set; }

        public float3 BoundsMax { get; private set; }

        public string Describe { get; private set; }

        public Mesh FirstColliderMesh => _meshes.Count > 0 ? _meshes[0] : null;

        public static SandboxCharacterBody TryBuild(VpCpuGeometryStorage storage, string hullsJsonPath, string renderJsonPath, int sourceIndex)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (!File.Exists(hullsJsonPath) || !File.Exists(renderJsonPath))
            {
                return null;
            }

            var hulls = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(hullsJsonPath, Encoding.UTF8));
            var render = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(renderJsonPath, Encoding.UTF8));
            var built = new SandboxCharacterBody();
            try
            {
                built.BuildShape((List<object>)hulls["hulls"]);
                if (!built.TryAppendGeometry(storage, (List<object>)render["geometries"], sourceIndex))
                {
                    built.Dispose();
                    return null;
                }
            }
            catch
            {
                built.Dispose();
                throw;
            }

            built.Describe = "character m_8 (intake export): " + built.HullCount + " hulls, " + built.HullVertexCount + " hull vertices, "
                             + built.HullFaceCount + " hull faces; display " + built.RenderVertexCount + " vertices / "
                             + built.RenderIndexCount + " indices in " + built.SubmeshCount + " submeshes; bounds "
                             + built.BoundsMin + " .. " + built.BoundsMax + " (Blender Z-up local frame)";
            return built;
        }

        public void Taken()
        {
            Shape = null;
            _meshes.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Shape?.Dispose();
            Shape = null;
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_meshes[i]);
                }
            }

            _meshes.Clear();
            for (int i = 0; i < _native.Count; i++)
            {
                _native[i].Dispose();
            }

            _native.Clear();
        }

        // ---- the physics shape --------------------------------------------------------------------------------------

        private void BuildShape(List<object> hulls)
        {
            var corners = new List<float3>();
            var faceOffsets = new List<int>();
            var faceIndices = new List<int>();
            var faceEdges = new List<int>();
            var edges = new List<BrepEdge>();
            var ranges = new List<ConvexBrepRange>(hulls.Count);
            var colliderTriangles = new List<int[]>(hulls.Count);
            var cornerCounts = new List<int>(hulls.Count);
            for (int h = 0; h < hulls.Count; h++)
            {
                var hull = (Dictionary<string, object>)hulls[h];
                var vertices = (List<object>)hull["vertices"];
                var polygons = (List<object>)hull["polygons"];
                int vertexBase = corners.Count;
                int faceBase = faceOffsets.Count;
                int faceIndexBase = faceIndices.Count;
                int edgeBase = edges.Count;
                for (int v = 0; v < vertices.Count; v++)
                {
                    corners.Add(Float3((List<object>)vertices[v]));
                }

                var localOffsets = new int[polygons.Count + 1];
                var localIndices = new List<int>(polygons.Count * 3);
                int maxLoop = 3;
                var triangles = new List<int>(polygons.Count * 3);
                for (int f = 0; f < polygons.Count; f++)
                {
                    var polygon = (List<object>)polygons[f];
                    localOffsets[f] = localIndices.Count;
                    for (int k = 0; k < polygon.Count; k++)
                    {
                        localIndices.Add((int)(double)polygon[k]);
                    }

                    maxLoop = Math.Max(maxLoop, polygon.Count);

                    // Unity's collider mesh winding is clockwise seen from outside, the mirror of the B-rep's.
                    for (int k = 1; k + 1 < polygon.Count; k++)
                    {
                        triangles.Add((int)(double)polygon[0]);
                        triangles.Add((int)(double)polygon[k + 1]);
                        triangles.Add((int)(double)polygon[k]);
                    }
                }

                localOffsets[polygons.Count] = localIndices.Count;
                BuildEdges(localOffsets, localIndices.ToArray(), out int[] localFaceEdges, out BrepEdge[] localEdges);
                faceOffsets.AddRange(localOffsets);
                faceIndices.AddRange(localIndices);
                faceEdges.AddRange(localFaceEdges);
                edges.AddRange(localEdges);
                ranges.Add(new ConvexBrepRange
                {
                    vertexBase = vertexBase,
                    vertexCount = vertices.Count,
                    faceBase = faceBase,
                    faceCount = polygons.Count,
                    faceIndexBase = faceIndexBase,
                    faceIndexCount = localIndices.Count,
                    edgeBase = edgeBase,
                    edgeCount = localEdges.Length,
                    maxFaceLoop = maxLoop,
                });
                colliderTriangles.Add(triangles.ToArray());
                cornerCounts.Add(vertices.Count);
                HullVertexCount += vertices.Count;
                HullFaceCount += polygons.Count;
            }

            HullCount = hulls.Count;
            Shape = NewShape(
                corners.ToArray(), faceOffsets.ToArray(), faceIndices.ToArray(), faceEdges.ToArray(), edges.ToArray(),
                ranges, colliderTriangles, cornerCounts);
        }

        private PhysicsOwnerShape NewShape(
            float3[] corners, int[] faceOffsets, int[] faceIndices, int[] faceEdges, BrepEdge[] edges,
            List<ConvexBrepRange> ranges, List<int[]> colliderTriangles, List<int> cornerCounts)
        {
            var vertices = new NativeArray<float3>(corners, Allocator.Persistent);
            var offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
            var indices = new NativeArray<int>(faceIndices, Allocator.Persistent);
            var edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
            var edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);
            _native.Add(vertices);
            _native.Add(offsets);
            _native.Add(indices);
            _native.Add(edgesOfFaces);
            _native.Add(edgeTable);
            var bank = new ConvexBrepBank
            {
                vertices = (float3*)NativeArrayUnsafeUtility.GetUnsafePtr(vertices),
                faceOffsets = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(offsets),
                faceIndices = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(indices),
                faceEdges = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(edgesOfFaces),
                edges = (BrepEdge*)NativeArrayUnsafeUtility.GetUnsafePtr(edgeTable),
            };

            for (int c = 0; c < ranges.Count; c++)
            {
                var mesh = new Mesh { name = "Sandbox character hull " + c, hideFlags = HideFlags.HideAndDontSave };
                var meshVertices = new Vector3[cornerCounts[c]];
                for (int i = 0; i < meshVertices.Length; i++)
                {
                    meshVertices[i] = corners[ranges[c].vertexBase + i];
                }

                mesh.vertices = meshVertices;
                mesh.triangles = colliderTriangles[c];
                UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                _meshes.Add(mesh);
            }

            return PhysicsOwnerShape.Authored(bank, ranges, _meshes, PhysicsShapeSource.External(), float4x4.identity);
        }

        private static void BuildEdges(int[] faceOffsets, int[] faceIndices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var found = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[faceIndices.Length];
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f];
                int count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k];
                    int b = faceIndices[from + ((k + 1) % count)];
                    int lo = math.min(a, b);
                    int hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        found.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = table[at];
                    if (a == lo)
                    {
                        if (edge.f0 < 0)
                        {
                            edge.f0 = f;
                        }
                    }
                    else if (edge.f1 < 0)
                    {
                        edge.f1 = f;
                    }

                    table[at] = edge;
                    faceEdges[from + k] = at;
                }
            }

            edges = table.ToArray();
        }

        // ---- the display geometry -----------------------------------------------------------------------------------

        private bool TryAppendGeometry(VpCpuGeometryStorage storage, List<object> geometries, int sourceIndex)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            int topologyBase = 0;
            var min = new float3(float.PositiveInfinity);
            var max = new float3(float.NegativeInfinity);
            for (int g = 0; g < geometries.Count; g++)
            {
                var geometry = (Dictionary<string, object>)geometries[g];
                var positions = (List<object>)geometry["positions"];
                var normals = (List<object>)geometry["normals"];
                var uv0 = (List<object>)geometry["uv0"];
                var topologyOfVertex = (List<object>)geometry["topologyOfVertex"];
                var localIndices = (List<object>)geometry["indices"];
                var submeshIndexCounts = (List<object>)geometry["submeshIndexCounts"];
                int topologyVertexCount = (int)(double)geometry["topologyVertexCount"];
                int vertexBase = vertices.Count;
                for (int v = 0; v < positions.Count; v++)
                {
                    float3 p = Float3((List<object>)positions[v]);
                    min = math.min(min, p);
                    max = math.max(max, p);
                    vertices.Add(new VpRenderVertex
                    {
                        position = p,
                        normal = Float3((List<object>)normals[v]),
                        uv0 = new float2((float)(double)uv0[2 * v], (float)(double)uv0[(2 * v) + 1]),
                    });
                    topology.Add(topologyBase + (int)(double)topologyOfVertex[v]);
                }

                int indexBase = indices.Count;
                for (int i = 0; i < localIndices.Count; i++)
                {
                    indices.Add((uint)(vertexBase + (int)(double)localIndices[i]));
                }

                int offset = indexBase;
                for (int s = 0; s < submeshIndexCounts.Count; s++)
                {
                    int count = (int)(double)submeshIndexCounts[s];
                    submeshes.Add(new VpGeometrySubmesh(offset, count, sourceIndex));
                    offset += count;
                }

                topologyBase += topologyVertexCount;
            }

            RenderVertexCount = vertices.Count;
            RenderIndexCount = indices.Count;
            SubmeshCount = submeshes.Count;
            BoundsMin = min;
            BoundsMax = max;
            bool taken = storage.TryAppendCuttable(
                vertices.ToArray(), indices.ToArray(), topology.ToArray(), topologyBase, submeshes.ToArray(),
                out VpStoredGeometry geometryHandle, out VpCutInputVerdict verdict);
            if (!taken)
            {
                Debug.LogWarning("Sandbox character: the storage would not take the geometry as cuttable: " + verdict);
                return false;
            }

            Geometry = geometryHandle;
            return true;
        }

        private static float3 Float3(List<object> xyz)
        {
            return new float3((float)(double)xyz[0], (float)(double)xyz[1], (float)(double)xyz[2]);
        }

        /// <summary>The smallest JSON reader that reads the two intake files: objects, arrays, numbers, strings, literals.</summary>
        private static class MiniJson
        {
            public static object Parse(string text)
            {
                int at = 0;
                object value = ReadValue(text, ref at);
                return value;
            }

            private static object ReadValue(string s, ref int at)
            {
                SkipSpace(s, ref at);
                char c = s[at];
                if (c == '{')
                {
                    at++;
                    var map = new Dictionary<string, object>();
                    SkipSpace(s, ref at);
                    if (s[at] == '}')
                    {
                        at++;
                        return map;
                    }

                    while (true)
                    {
                        SkipSpace(s, ref at);
                        string key = ReadString(s, ref at);
                        SkipSpace(s, ref at);
                        at++; // ':'
                        map[key] = ReadValue(s, ref at);
                        SkipSpace(s, ref at);
                        if (s[at++] == '}')
                        {
                            return map;
                        }
                    }
                }

                if (c == '[')
                {
                    at++;
                    var list = new List<object>();
                    SkipSpace(s, ref at);
                    if (s[at] == ']')
                    {
                        at++;
                        return list;
                    }

                    while (true)
                    {
                        list.Add(ReadValue(s, ref at));
                        SkipSpace(s, ref at);
                        if (s[at++] == ']')
                        {
                            return list;
                        }
                    }
                }

                if (c == '"')
                {
                    return ReadString(s, ref at);
                }

                if (c == 't')
                {
                    at += 4;
                    return true;
                }

                if (c == 'f')
                {
                    at += 5;
                    return false;
                }

                if (c == 'n')
                {
                    at += 4;
                    return null;
                }

                int start = at;
                while (at < s.Length && "+-0123456789.eE".IndexOf(s[at]) >= 0)
                {
                    at++;
                }

                return double.Parse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private static string ReadString(string s, ref int at)
            {
                at++; // opening quote
                var sb = new StringBuilder();
                while (s[at] != '"')
                {
                    if (s[at] == '\\')
                    {
                        at++;
                        char e = s[at];
                        sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                        at++;
                        continue;
                    }

                    sb.Append(s[at++]);
                }

                at++; // closing quote
                return sb.ToString();
            }

            private static void SkipSpace(string s, ref int at)
            {
                while (at < s.Length && (s[at] == ' ' || s[at] == '\n' || s[at] == '\r' || s[at] == '\t' || s[at] == '﻿'))
                {
                    at++;
                }
            }
        }
    }
}
