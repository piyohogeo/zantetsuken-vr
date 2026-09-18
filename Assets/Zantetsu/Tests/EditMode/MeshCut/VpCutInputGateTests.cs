using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The shared geometry input gate of DESIGN 6.2 (T-084), and the line it draws between an ordinary append, which
    /// any geometry with a topology mapping may take and which can be displayed, and a cuttable one, which only a
    /// geometry that passes the gate takes and which alone the cut input accepts.
    /// <para>
    /// The accepted fixtures are the existing synthetic shapes the cut kernel's own tests use; the refused ones are
    /// small hand-made shapes, each broken in one way. Nothing here repeats the refusals as stencil drawing tests.
    /// </para>
    /// </summary>
    public class VpCutInputGateTests
    {
        private sealed class Input
        {
            public string Name;
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public int TopologyVertexCount;
            public VpGeometrySubmesh[] Submeshes;

            public VpCutInputVerdict Check()
            {
                return VpCutInputGate.Check(Vertices, Indices, TopologyOfVertex, TopologyVertexCount, Submeshes);
            }

            public Input Copy()
            {
                return new Input
                {
                    Name = Name,
                    Vertices = (VpRenderVertex[])Vertices?.Clone(),
                    Indices = (uint[])Indices?.Clone(),
                    TopologyOfVertex = (int[])TopologyOfVertex?.Clone(),
                    TopologyVertexCount = TopologyVertexCount,
                    Submeshes = (VpGeometrySubmesh[])Submeshes?.Clone(),
                };
            }
        }

        private static LogicalMeshBuilder.AttributeOptions Smooth => new LogicalMeshBuilder.AttributeOptions();

        private static LogicalMeshBuilder.AttributeOptions Seams =>
            new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true };

        private static Input From(string name, LogicalMeshBuilder builder, LogicalMeshBuilder.AttributeOptions options)
        {
            SyntheticMesh mesh = builder.Finish(options);
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            return new Input
            {
                Name = name,
                Vertices = mesh.Vertices,
                Indices = mesh.Indices,
                TopologyOfVertex = mesh.TopologyOfVertex,
                TopologyVertexCount = mesh.TopologyVertexCount,
                Submeshes = submeshes,
            };
        }

        /// <summary>A unit cube on eight corners, wound consistently; the base the broken shapes start from.</summary>
        private static LogicalMeshBuilder Cube(float3 offset)
        {
            var b = new LogicalMeshBuilder();
            float3[] corners =
            {
                new float3(0, 0, 0), new float3(1, 0, 0), new float3(1, 1, 0), new float3(0, 1, 0),
                new float3(0, 0, 1), new float3(1, 0, 1), new float3(1, 1, 1), new float3(0, 1, 1),
            };
            foreach (float3 c in corners)
            {
                b.AddVertex(c + offset);
            }

            b.AddQuad(0, 4, 5, 1);
            b.AddQuad(1, 5, 6, 2);
            b.AddQuad(2, 6, 7, 3);
            b.AddQuad(3, 7, 4, 0);
            b.AddQuad(0, 1, 2, 3);
            b.AddQuad(4, 7, 6, 5);
            return b;
        }

        /// <summary>A tetrahedron on the given four corner ids, wound consistently.</summary>
        private static void AddTetrahedron(LogicalMeshBuilder b, int p0, int p1, int p2, int p3)
        {
            b.AddTriangle(p0, p2, p1);
            b.AddTriangle(p0, p1, p3);
            b.AddTriangle(p0, p3, p2);
            b.AddTriangle(p1, p2, p3);
        }

        private static void RemoveTriangle(LogicalMeshBuilder b, int triangle)
        {
            b.Triangles.RemoveRange(3 * triangle, 3);
            b.Submesh.RemoveAt(triangle);
        }

        private static IEnumerable<Input> Accepted()
        {
            yield return From("closed box", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero), Smooth);

            LogicalMeshBuilder reversed = SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero);
            reversed.Reverse();
            yield return From("fully reversed box", reversed, Smooth);

            yield return From("several closed components", SyntheticGeometry.BarField(3, 1, 0.5f, 1f, float3.zero), Smooth);
            yield return From("box with attribute seams", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero, true), Seams);
            yield return From("sphere with attribute seams", SyntheticGeometry.UvSphere(12, 6, 0.5f, float3.zero), Seams);

            LogicalMeshBuilder coincident = SyntheticGeometry.Box(1, new float3(1, 1, 1), float3.zero);
            coincident.Append(SyntheticGeometry.Box(1, new float3(1, 1, 1), float3.zero));
            yield return From("coincident duplicate on its own topology", coincident, Smooth);

            yield return From("nested shells", SyntheticGeometry.NestedShells(float3.zero), Smooth);
            yield return From("self-intersecting tube", SyntheticGeometry.LemniscateTube(24, 3, 1f, 0.5f, float3.zero), Smooth);
            yield return From("box with zero-area triangles", SyntheticGeometry.BoxWithZeroAreaTriangles(new float3(1, 1, 1), float3.zero), Smooth);
            yield return From("opposite coincident pair", SyntheticGeometry.OppositeCoincidentPair(1f, float3.zero), Smooth);
        }

        private static IEnumerable<(Input input, VpCutInputRejection expected)> Refused()
        {
            LogicalMeshBuilder open = Cube(float3.zero);
            RemoveTriangle(open, 11);
            yield return (From("open boundary", open, Smooth), VpCutInputRejection.EdgeFaceCount);

            // a fin on one edge of a tetrahedron: that edge has three faces (the fin also opens two boundary edges)
            var fin = new LogicalMeshBuilder();
            fin.AddVertex(0, 0, 0);
            fin.AddVertex(1, 0, 0);
            fin.AddVertex(0, 1, 0);
            fin.AddVertex(0, 0, 1);
            fin.AddVertex(0.5f, -1, 0.5f);
            AddTetrahedron(fin, 0, 1, 2, 3);
            fin.AddTriangle(0, 1, 4);
            yield return (From("an edge with three faces", fin, Smooth), VpCutInputRejection.EdgeFaceCount);

            // two closed tetrahedra sharing one edge: that edge has four faces, every other edge two
            var shared = new LogicalMeshBuilder();
            shared.AddVertex(0, 0, 0);
            shared.AddVertex(1, 0, 0);
            shared.AddVertex(0, 1, 0);
            shared.AddVertex(0, 0, 1);
            shared.AddVertex(0, -1, 0);
            shared.AddVertex(0, 0, -1);
            AddTetrahedron(shared, 0, 1, 2, 3);
            AddTetrahedron(shared, 0, 1, 4, 5);
            yield return (From("an edge with four faces", shared, Smooth), VpCutInputRejection.EdgeFaceCount);

            // one face of the cube split through the midpoint of an edge its neighbour does not split: geometrically
            // watertight, but on the topology the long edge and the two short ones each have a single face
            LogicalMeshBuilder tee = Cube(float3.zero);
            int midpoint = tee.AddVertex(0.5f, 0, 0);
            RemoveTriangle(tee, 8); // (0, 1, 2) of the quad (0, 1, 2, 3)
            tee.AddTriangle(0, midpoint, 2);
            tee.AddTriangle(midpoint, 1, 2);
            yield return (From("a T-junction", tee, Smooth), VpCutInputRejection.EdgeFaceCount);

            LogicalMeshBuilder flipped = Cube(float3.zero);
            int t = 5;
            (flipped.Triangles[3 * t + 1], flipped.Triangles[3 * t + 2]) = (flipped.Triangles[3 * t + 2], flipped.Triangles[3 * t + 1]);
            yield return (From("one triangle wound against its neighbours", flipped, Smooth), VpCutInputRejection.EdgeSameDirection);

            // two closed tetrahedra sharing one vertex and no edge: every edge is fine, the vertex has two fans
            var bowtie = new LogicalMeshBuilder();
            bowtie.AddVertex(0, 0, 0);
            bowtie.AddVertex(1, 0, 0);
            bowtie.AddVertex(0, 1, 0);
            bowtie.AddVertex(0, 0, 1);
            bowtie.AddVertex(-1, 0, 0);
            bowtie.AddVertex(0, -1, 0);
            bowtie.AddVertex(0, 0, -1);
            AddTetrahedron(bowtie, 0, 1, 2, 3);
            AddTetrahedron(bowtie, 0, 4, 5, 6);
            yield return (From("a vertex shared by two fans", bowtie, Smooth), VpCutInputRejection.VertexMultipleFans);

            Input seams = From("seams", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero), Seams);
            Input mismatch = seams.Copy();
            mismatch.Name = "a control point with two positions";
            int moved = SecondRenderVertexOfAControlPoint(mismatch);
            mismatch.Vertices[moved].position += new Vector3(0f, 1e-4f, 0f);
            yield return (mismatch, VpCutInputRejection.PositionMismatch);

            Input closed = From("closed", Cube(float3.zero), Smooth);
            yield return (Damaged(closed, "a NaN position", i => i.Vertices[i.Indices[0]].position.x = float.NaN), VpCutInputRejection.NonFinite);
            yield return (Damaged(closed, "an infinite normal", i => i.Vertices[i.Indices[0]].normal.y = float.PositiveInfinity), VpCutInputRejection.NonFinite);
            yield return (Damaged(closed, "a NaN uv", i => i.Vertices[i.Indices[0]].uv0.x = float.NaN), VpCutInputRejection.NonFinite);
            yield return (Damaged(closed, "an index past the vertices", i => i.Indices[4] = (uint)i.Vertices.Length), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "a topology id past the count", i => i.TopologyOfVertex[0] = i.TopologyVertexCount), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "a topology map of the wrong length", i => Array.Resize(ref i.TopologyOfVertex, i.TopologyOfVertex.Length - 1)), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "a negative topology count", i => i.TopologyVertexCount = -1), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "indices that are not whole triangles", i => Array.Resize(ref i.Indices, i.Indices.Length - 1)), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "submeshes that do not cover the indices", i => i.Submeshes[0] = new VpGeometrySubmesh(0, i.Submeshes[0].indexCount - 3, 0)), VpCutInputRejection.InvalidReference);
            yield return (Damaged(closed, "a triangle naming one topology vertex twice", i => i.Indices[1] = i.Indices[0]), VpCutInputRejection.TriangleRepeatsTopologyVertex);
            yield return (Damaged(closed, "a missing array", i => i.Indices = null), VpCutInputRejection.MissingArray);
        }

        private static Input Damaged(Input source, string name, Action<Input> damage)
        {
            Input copy = source.Copy();
            copy.Name = name;
            damage(copy);
            return copy;
        }

        /// <summary>A used render vertex whose control point another used render vertex also carries.</summary>
        private static int SecondRenderVertexOfAControlPoint(Input input)
        {
            var first = new Dictionary<int, int>();
            foreach (uint index in input.Indices)
            {
                int v = (int)index;
                int topology = input.TopologyOfVertex[v];
                if (first.TryGetValue(topology, out int earlier))
                {
                    if (earlier != v)
                    {
                        return v;
                    }
                }
                else
                {
                    first[topology] = v;
                }
            }

            throw new InvalidOperationException("the fixture has no seam");
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(8192, 32768, 16, 64, 64, Allocator.Persistent);
        }

        private static bool AppendCuttable(VpCpuGeometryStorage storage, Input input, out VpStoredGeometry geometry, out VpCutInputVerdict verdict)
        {
            return storage.TryAppendCuttable(
                input.Vertices, input.Indices, input.TopologyOfVertex, input.TopologyVertexCount, input.Submeshes, out geometry, out verdict);
        }

        private static float4 PlaneThroughTheMiddle()
        {
            return SyntheticGeometry.Plane(math.normalize(new float3(0.37f, 0.61f, -0.7f)), new float3(0.5f, 0.5f, 0.5f));
        }

        [Test]
        public void EveryAcceptedShape_PassesTheGate_AndIsAppendedInItsOwnOrientation()
        {
            foreach (Input input in Accepted())
            {
                VpCutInputVerdict verdict = input.Check();
                Assert.That(verdict.Accepted, Is.True, input.Name + ": " + verdict);

                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    Assert.That(AppendCuttable(storage, input, out VpStoredGeometry geometry, out _), Is.True, input.Name + ": append");
                    Assert.That(geometry.cutInputAccepted, Is.True, input.Name + ": recorded as a cut input");

                    // the orientation is the input's own: nothing is reversed, normalised or reordered
                    Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly stored), Is.True);
                    for (int i = 0; i < input.Indices.Length; i++)
                    {
                        Assert.That(stored[i], Is.EqualTo(input.Indices[i] + (uint)geometry.vertexStart), input.Name + ": index " + i);
                    }

                    Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                }
            }
        }

        [Test]
        public void EveryRefusedShape_IsRefusedForItsOwnReason_BeforeAnythingIsAppended()
        {
            foreach ((Input input, VpCutInputRejection expected) in Refused())
            {
                VpCutInputVerdict verdict = input.Check();
                Assert.That(verdict.rejection, Is.EqualTo(expected), input.Name + ": " + verdict);

                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    Assert.That(AppendCuttable(storage, input, out VpStoredGeometry geometry, out VpCutInputVerdict appendVerdict), Is.False, input.Name);
                    Assert.That(appendVerdict.rejection, Is.EqualTo(expected), input.Name + ": the append gives the gate's reason");
                    Assert.That(geometry, Is.EqualTo(default(VpStoredGeometry)), input.Name + ": no geometry");
                    Assert.That(storage.VertexCount, Is.Zero, input.Name + ": no vertex");
                    Assert.That(storage.SubmeshCount, Is.Zero, input.Name + ": no submesh");
                    Assert.That(storage.VertexBlockCount, Is.Zero, input.Name + ": no block");
                }
            }
        }

        [Test]
        public void AnOpenMesh_IsAppendedAndShownAsAnOrdinaryGeometry_ButRefusedByTheCutInput()
        {
            LogicalMeshBuilder builder = Cube(float3.zero);
            RemoveTriangle(builder, 11);
            Input open = From("open", builder, Smooth);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(
                    storage.TryAppendPrepared(open.Vertices, open.Indices, open.TopologyOfVertex, open.TopologyVertexCount, open.Submeshes, out VpStoredGeometry ordinary),
                    Is.True,
                    "an open mesh is still an ordinary append");
                Assert.That(ordinary.hasTopology, Is.True, "it keeps its topology mapping");
                Assert.That(ordinary.cutInputAccepted, Is.False, "but it is not a cut input");

                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, ordinary, out Mesh mesh), Is.True, "and it can be displayed");
                UnityEngine.Object.DestroyImmediate(mesh);

                Assert.That(VpStorageCutInput.TryAcquire(storage, ordinary, out VpStorageCutInput refused), Is.False, "the cut input refuses it");
                Assert.That(refused, Is.Null);
            }
        }

        [Test]
        public void TheSameOpenMesh_HandedInAsCuttable_IsRefusedBeforeItIsRegistered()
        {
            LogicalMeshBuilder builder = Cube(float3.zero);
            RemoveTriangle(builder, 11);
            Input open = From("open", builder, Smooth);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(AppendCuttable(storage, open, out VpStoredGeometry geometry, out VpCutInputVerdict verdict), Is.False);
                Assert.That(verdict.rejection, Is.EqualTo(VpCutInputRejection.EdgeFaceCount), verdict.ToString());
                Assert.That(geometry, Is.EqualTo(default(VpStoredGeometry)));
                Assert.That(storage.VertexCount, Is.Zero, "nothing was appended");
            }
        }

        [Test]
        public void ATopologyMappingAlone_DoesNotMakeAGeometryCuttable()
        {
            Input closed = From("closed", Cube(float3.zero), Smooth);
            Assert.That(closed.Check().Accepted, Is.True, "a shape that would pass the gate");
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(
                    storage.TryAppendPrepared(closed.Vertices, closed.Indices, closed.TopologyOfVertex, closed.TopologyVertexCount, closed.Submeshes, out VpStoredGeometry ordinary),
                    Is.True);
                Assert.That(VpStorageCutInput.TryAcquire(storage, ordinary, out VpStorageCutInput refused), Is.False, "appended the ordinary way, it is not a cut input");
                Assert.That(refused, Is.Null);

                // a copy that claims acceptance is not one of the storage's geometries: the flag is checked against its record
                var forged = new VpStoredGeometry(
                    ordinary.vertexStart, ordinary.vertexCount, ordinary.indexRange, ordinary.hasTopology, ordinary.topologyVertexCount,
                    ordinary.submeshStart, ordinary.submeshCount, ordinary.blockStart, ordinary.blockCount, true);
                Assert.That(VpStorageCutInput.TryAcquire(storage, forged, out VpStorageCutInput forgedInput), Is.False, "a forged acceptance");
                Assert.That(forgedInput, Is.Null);
            }
        }

        [Test]
        public void AnAcceptedGeometry_IsTakenByTheCutInput_AndWhatItsCutProducesIsCutAgainWithoutTheGate()
        {
            Input closed = From("seamed box", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero), Seams);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(AppendCuttable(storage, closed, out VpStoredGeometry parent, out _), Is.True);
                Assert.That(VpStorageCutInput.TryAcquire(storage, parent, out VpStorageCutInput input), Is.True, "the accepted geometry is a cut input");

                VpStorageCutResult first;
                using (input)
                {
                    Assert.That(VpStorageCut.TryExecute(storage, input, PlaneThroughTheMiddle(), out first), Is.True, "cut");
                    Assert.That(first.status, Is.EqualTo(VpStorageCutStatus.Ok));
                }

                foreach (VpStorageCutSide side in new[] { first.positive, first.negative })
                {
                    Assert.That(side.IsProduced, Is.True, "both sides are produced");
                    Assert.That(side.geometry.cutInputAccepted, Is.True, "the kernel's output inherits the acceptance");

                    // and is cut again, straight from the cut output, with no gate in between
                    Assert.That(VpStorageCutInput.TryAcquire(storage, side.geometry, out VpStorageCutInput childInput), Is.True, "the child is a cut input");
                    using (childInput)
                    {
                        float4 second = SyntheticGeometry.Plane(math.normalize(new float3(0.81f, -0.23f, 0.54f)), new float3(0.5f, 0.5f, 0.5f));
                        Assert.That(VpStorageCut.TryExecute(storage, childInput, second, out VpStorageCutResult again), Is.True, "cut again");
                        Assert.That(again.status, Is.EqualTo(VpStorageCutStatus.Ok));
                    }
                }
            }
        }

        [Test]
        public void Refusals_LeaveTheStoredGeometry_ItsReferences_AndTheCapacity_AsTheyWere()
        {
            Input closed = From("closed", Cube(float3.zero), Smooth);
            int vertices = closed.Vertices.Length;
            int indices = closed.Indices.Length;

            // room for exactly two of the closed shape, so any capacity a refusal took would make the second append fail
            using (VpCpuGeometryStorage storage = new VpCpuGeometryStorage(2 * vertices, 2 * indices, 2, 2 * closed.Submeshes.Length, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 2, 4);
                Assert.That(AppendCuttable(storage, closed, out VpStoredGeometry kept, out _), Is.True, "the one already stored");
                Assert.That(table.TryRegisterGeometryWithDisplayInstance(kept, out VpGeometryReference reference, out _), Is.True, "registered");
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;

                foreach ((Input input, VpCutInputRejection expected) in Refused())
                {
                    Assert.That(AppendCuttable(storage, input, out _, out VpCutInputVerdict verdict), Is.False, input.Name);
                    Assert.That(verdict.rejection, Is.EqualTo(expected), input.Name);
                }

                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "vertices");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), "submeshes");
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), "blocks");
                Assert.That(storage.TryGetTopology(kept, out NativeArray<int>.ReadOnly topology, out _), Is.True, "the stored geometry is still readable");
                Assert.That(topology.ToArray(), Is.EqualTo(closed.TopologyOfVertex), "with its own mapping");
                Assert.That(table.TryAddDisplayInstance(reference, out _), Is.True, "and its registration still takes an instance");
                Assert.That(VpStorageCutInput.TryAcquire(storage, kept, out VpStorageCutInput keptInput), Is.True, "and it is still a cut input");
                keptInput.Dispose();

                Assert.That(AppendCuttable(storage, closed, out _, out _), Is.True, "no refusal took any of the room for the second one");
            }
        }
    }
}
