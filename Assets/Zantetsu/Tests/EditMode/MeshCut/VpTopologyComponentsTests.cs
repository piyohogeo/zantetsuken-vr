using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// SPLIT-CANDIDATES-3: the topology connected components of a geometry's triangles, as they are counted where a geometry is
    /// made and kept by the storage -- the cut input gate's count for an initial geometry (closed, seamed, several shells,
    /// coincident shells on their own ids), each side's own count after a cut (its cap and the new vertex block included, a
    /// cut that changes the connectivity, a cut of a cut), unknown where they were not taken, and a count that stays with its
    /// own geometry when a handle is used again. Every expected number is taken apart from the code under test, by walking the
    /// stored triangles.
    /// </summary>
    public class VpTopologyComponentsTests
    {
        private bool _enabledBefore;

        [SetUp]
        public void SetUp()
        {
            _enabledBefore = VpTopologyComponents.Enabled;
            VpTopologyComponents.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            VpTopologyComponents.Enabled = _enabledBefore;
        }

        private sealed class Input
        {
            public string Name;
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public int TopologyVertexCount;
            public VpGeometrySubmesh[] Submeshes;
        }

        private static LogicalMeshBuilder.AttributeOptions Smooth => new LogicalMeshBuilder.AttributeOptions();

        private static LogicalMeshBuilder.AttributeOptions Seams => new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true };

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
                Name = name, Vertices = mesh.Vertices, Indices = mesh.Indices, TopologyOfVertex = mesh.TopologyOfVertex,
                TopologyVertexCount = mesh.TopologyVertexCount, Submeshes = submeshes,
            };
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(65536, 262144, 64, 256, 256, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Input input)
        {
            Assert.That(
                storage.TryAppendCuttable(input.Vertices, input.Indices, input.TopologyOfVertex, input.TopologyVertexCount, input.Submeshes, out VpStoredGeometry geometry, out VpCutInputVerdict verdict),
                Is.True, input.Name + ": " + verdict);
            return geometry;
        }

        // The expected count, apart from the code under test: the triangles the geometry's own indices name, joined where they
        // share a topology id of the storage's map, counted by a flood over the triangles.
        private static int ComponentsByFlood(VpCpuGeometryStorage storage, VpStoredGeometry geometry, out int seamVertices, out int unreferencedVertices)
        {
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices), Is.True);
            NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
            int triangles = indices.Length / 3;
            var trianglesOf = new Dictionary<int, List<int>>();
            var renderVerticesOf = new Dictionary<int, HashSet<uint>>();
            for (int t = 0; t < triangles; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    uint v = indices[(3 * t) + k];
                    int id = topology[(int)v];
                    if (!trianglesOf.TryGetValue(id, out List<int> list)) trianglesOf[id] = list = new List<int>();
                    list.Add(t);
                    if (!renderVerticesOf.TryGetValue(id, out HashSet<uint> set)) renderVerticesOf[id] = set = new HashSet<uint>();
                    set.Add(v);
                }
            }

            seamVertices = 0;
            foreach (KeyValuePair<int, HashSet<uint>> pair in renderVerticesOf) if (pair.Value.Count > 1) seamVertices++;
            Assert.That(storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
            int named = 0, total = 0;
            foreach (KeyValuePair<int, HashSet<uint>> pair in renderVerticesOf) named += pair.Value.Count;
            for (int b = 0; b < blocks.Length; b++) total += blocks[b].vertexCount;
            unreferencedVertices = total - named;

            var seen = new bool[triangles];
            var stack = new Stack<int>();
            int components = 0;
            for (int t = 0; t < triangles; t++)
            {
                if (seen[t]) continue;
                components++;
                seen[t] = true;
                stack.Push(t);
                while (stack.Count > 0)
                {
                    int at = stack.Pop();
                    for (int k = 0; k < 3; k++)
                    {
                        foreach (int other in trianglesOf[topology[(int)indices[(3 * at) + k]]])
                        {
                            if (seen[other]) continue;
                            seen[other] = true;
                            stack.Push(other);
                        }
                    }
                }
            }

            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
            return components;
        }

        private static int ComponentsByFlood(VpCpuGeometryStorage storage, VpStoredGeometry geometry) => ComponentsByFlood(storage, geometry, out _, out _);

        private static VpStorageCutResult Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane, string what)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, what + ": a cut input");
            using (input)
            {
                Assert.That(VpStorageCut.TryExecute(storage, input, plane, out VpStorageCutResult result), Is.True, what + ": cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok), what);
                return result;
            }
        }

        [Test]
        public void TheGate_CountsAnInitialGeometrysComponents_AndTheStorageKeepsThem()
        {
            LogicalMeshBuilder coincident = SyntheticGeometry.Box(1, new float3(1, 1, 1), float3.zero);
            coincident.Append(SyntheticGeometry.Box(1, new float3(1, 1, 1), float3.zero));
            var cases = new (Input input, int expected)[]
            {
                (From("closed box", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero), Smooth), 1),
                (From("box with material and attribute seams", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero, true), Seams), 1),
                (From("sphere with attribute seams", SyntheticGeometry.UvSphere(12, 6, 0.5f, float3.zero), Seams), 1),
                (From("three closed bars", SyntheticGeometry.BarField(3, 1, 0.5f, 1f, float3.zero), Smooth), 3),
                (From("two shells at one place, each on its own ids", coincident, Smooth), 2),
                (From("nested shells", SyntheticGeometry.NestedShells(float3.zero), Smooth), 2),
            };
            foreach ((Input input, int expected) in cases)
            {
                VpCutInputVerdict verdict = VpCutInputGate.Check(input.Vertices, input.Indices, input.TopologyOfVertex, input.TopologyVertexCount, input.Submeshes);
                Assert.That(verdict.Accepted, Is.True, input.Name + ": " + verdict);
                Assert.That(verdict.TopologyComponents, Is.EqualTo(expected), input.Name + ": the gate's count");
                Assert.That(VpTopologyComponents.CountLocal(input.Indices, input.TopologyOfVertex, input.TopologyVertexCount), Is.EqualTo(expected), input.Name + ": the count on the arrays");
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    VpStoredGeometry geometry = Append(storage, input);
                    int flood = ComponentsByFlood(storage, geometry, out int seams, out _);
                    Assert.That(flood, Is.EqualTo(expected), input.Name + ": the layout");
                    Assert.That(storage.TopologyComponentsOf(geometry), Is.EqualTo(expected), input.Name + ": kept with the geometry");
                    if (input.Name.Contains("seams")) Assert.That(seams, Is.GreaterThan(0), input.Name + ": the layout has seams (several render vertices of one topology vertex)");
                }
            }
        }

        [Test]
        public void AVerifiedConnectivity_GivesItsCountAgain()
        {
            Input bars = From("three closed bars", SyntheticGeometry.BarField(3, 1, 0.5f, 1f, float3.zero), Smooth);
            var verified = new VpCutInputConnectivity();
            VpCutInputVerdict first = VpCutInputGate.Check(bars.Vertices, bars.Indices, bars.TopologyOfVertex, bars.TopologyVertexCount, bars.Submeshes, verified);
            VpCutInputVerdict again = VpCutInputGate.Check(bars.Vertices, bars.Indices, bars.TopologyOfVertex, bars.TopologyVertexCount, bars.Submeshes, verified);
            Assert.That(verified.Reused, Is.EqualTo(1), "the layout: the second check was answered by the kept connectivity");
            Assert.That(first.TopologyComponents, Is.EqualTo(3));
            Assert.That(again.TopologyComponents, Is.EqualTo(3), "the kept connectivity carries its count");
        }

        [Test]
        public void SwitchedOff_NothingIsCounted_AndEveryCountIsUnknown()
        {
            VpTopologyComponents.Enabled = false;
            Input box = From("closed box", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero), Smooth);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry geometry = Append(storage, box);
                Assert.That(storage.TopologyComponentsOf(geometry), Is.EqualTo(VpTopologyComponents.Unknown));
                VpStorageCutResult cut = Cut(storage, geometry, SyntheticGeometry.Plane(math.normalize(new float3(0.37f, 0.61f, -0.7f)), new float3(0.5f, 0.5f, 0.5f)), "box");
                Assert.That(storage.TopologyComponentsOf(cut.positive.geometry), Is.EqualTo(VpTopologyComponents.Unknown));
                Assert.That(storage.TopologyComponentsOf(cut.negative.geometry), Is.EqualTo(VpTopologyComponents.Unknown));
                Assert.That(storage.TopologyComponentsOf(default), Is.EqualTo(VpTopologyComponents.Unknown), "no geometry: unknown");

                // An unknown count can be given afterwards, and is the geometry's own.
                Assert.That(storage.TrySetTopologyComponents(cut.positive.geometry, 1), Is.True);
                Assert.That(storage.TopologyComponentsOf(cut.positive.geometry), Is.EqualTo(1));
                Assert.That(storage.TopologyComponentsOf(cut.negative.geometry), Is.EqualTo(VpTopologyComponents.Unknown), "the other side's is its own");
            }
        }

        [Test]
        public void EachSideOfACut_HasItsOwnCount_OnItsFinalIndices_CapAndNewVerticesIncluded()
        {
            var shapes = new[]
            {
                From("closed box", SyntheticGeometry.Box(3, new float3(1, 1, 1), float3.zero), Smooth),
                From("box with material and attribute seams", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero, true), Seams),
                From("sphere with attribute seams", SyntheticGeometry.UvSphere(12, 6, 0.5f, new float3(0.5f, 0.5f, 0.5f)), Seams),
                From("three closed bars", SyntheticGeometry.BarField(3, 1, 0.5f, 1f, float3.zero), Smooth),
                From("star prism", SyntheticGeometry.StarPrism(5, 1f, 0.4f, 1f, 2, float3.zero), Smooth),
                From("torus", SyntheticGeometry.Torus(16, 8, 1f, 0.3f, float3.zero), Smooth),
            };
            var planes = new List<float4>();
            foreach (float3 normal in new[] { new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1), math.normalize(new float3(0.37f, 0.61f, -0.7f)), math.normalize(new float3(1, 1, 0)) })
            {
                foreach (float offset in new[] { -0.75f, -0.55f, -0.2f, 0.13f, 0.5f, 0.62f, 0.83f })
                {
                    planes.Add(SyntheticGeometry.Plane(normal, normal * offset));
                }
            }

            int cuts = 0, withCap = 0, connectivityChanged = 0, severalBlocks = 0;
            foreach (Input shape in shapes)
            {
                foreach (float4 plane in planes)
                {
                    using (VpCpuGeometryStorage storage = NewStorage())
                    {
                        VpStoredGeometry parent = Append(storage, shape);
                        int parentCount = storage.TopologyComponentsOf(parent);
                        Assert.That(parentCount, Is.EqualTo(ComponentsByFlood(storage, parent)), shape.Name);
                        VpStorageCutResult cut = Cut(storage, parent, plane, shape.Name);
                        if (!cut.positive.IsProduced || !cut.negative.IsProduced)
                        {
                            // The plane missed: the input is borrowed back as it is, with the count it had.
                            VpStorageCutSide whole = cut.positive.kind == VpStorageCutSideKind.ReusesInput ? cut.positive : cut.negative;
                            Assert.That(storage.TopologyComponentsOf(whole.geometry), Is.EqualTo(parentCount), shape.Name + ": the same geometry, the same count");
                            continue;
                        }

                        cuts++;
                        if (cut.kernel.capTriangles > 0) withCap++;
                        foreach (VpStorageCutSide side in new[] { cut.positive, cut.negative })
                        {
                            int expected = ComponentsByFlood(storage, side.geometry, out _, out int unreferenced);
                            Assert.That(storage.TopologyComponentsOf(side.geometry), Is.EqualTo(expected), shape.Name + ", plane " + plane + ": the side's own count");
                            Assert.That(expected, Is.GreaterThanOrEqualTo(1));
                            Assert.That(unreferenced, Is.GreaterThan(0), shape.Name + ": the layout -- the side names only a part of the vertices it inherits");
                            if (expected != parentCount) connectivityChanged++;
                            if (side.geometry.blockCount >= 2) severalBlocks++;
                        }

                        Assert.That(storage.TopologyComponentsOf(parent), Is.EqualTo(parentCount), shape.Name + ": the parent's count is its own still");

                        // A cut of a cut: the child's vertices are the parent's blocks and the block its cut appended.
                        VpStoredGeometry child = cut.positive.geometry;
                        float4 second = SyntheticGeometry.Plane(math.normalize(new float3(0.81f, -0.23f, 0.54f)), new float3(0.31f, 0.27f, 0.36f));
                        VpStorageCutResult again = Cut(storage, child, second, shape.Name + ", again");
                        if (again.positive.IsProduced && again.negative.IsProduced)
                        {
                            foreach (VpStorageCutSide side in new[] { again.positive, again.negative })
                            {
                                Assert.That(storage.TopologyComponentsOf(side.geometry), Is.EqualTo(ComponentsByFlood(storage, side.geometry)), shape.Name + ": a cut of a cut");
                                if (side.geometry.blockCount >= 3) severalBlocks++;
                            }
                        }
                    }
                }
            }

            TestContext.Out.WriteLine("cuts " + cuts + ", with a cap " + withCap + ", sides whose count differs from the parent's " + connectivityChanged + ", sides over several vertex blocks " + severalBlocks);
            Assert.That(cuts, Is.GreaterThan(40), "the layout: the planes cut the shapes");
            Assert.That(withCap, Is.GreaterThan(40), "the layout: cuts with caps");
            Assert.That(connectivityChanged, Is.GreaterThan(0), "the layout: a cut that changes the connectivity is among them");
            Assert.That(severalBlocks, Is.GreaterThan(0), "the layout: sides over several vertex blocks");
        }

        [Test]
        public void ACutThatParts_AOnePieceShape_GivesTheSideItsSeveralComponents()
        {
            // A star prism: one component. A plane across two of its points leaves those two tips apart on one side.
            Input star = From("star prism", SyntheticGeometry.StarPrism(5, 1f, 0.4f, 1f, 2, float3.zero), Smooth);
            bool found = false;
            for (int step = 0; step < 72 && !found; step++)
            {
                float angle = step * (math.PI / 36f);
                var normal = new float3(math.cos(angle), math.sin(angle), 0f);
                foreach (float offset in new[] { 0.45f, 0.55f, 0.65f })
                {
                    using (VpCpuGeometryStorage storage = NewStorage())
                    {
                        VpStoredGeometry parent = Append(storage, star);
                        Assert.That(storage.TopologyComponentsOf(parent), Is.EqualTo(1), "the layout: one piece");
                        VpStorageCutResult cut = Cut(storage, parent, SyntheticGeometry.Plane(normal, normal * offset), "star");
                        if (!cut.positive.IsProduced || !cut.negative.IsProduced) continue;
                        int positive = storage.TopologyComponentsOf(cut.positive.geometry), negative = storage.TopologyComponentsOf(cut.negative.geometry);
                        Assert.That(positive, Is.EqualTo(ComponentsByFlood(storage, cut.positive.geometry)));
                        Assert.That(negative, Is.EqualTo(ComponentsByFlood(storage, cut.negative.geometry)));
                        if (math.max(positive, negative) >= 2 && math.min(positive, negative) == 1)
                        {
                            found = true;
                            TestContext.Out.WriteLine("angle step " + step + ", offset " + offset + ": " + positive + " and " + negative + " components");
                            break;
                        }
                    }
                }
            }

            Assert.That(found, Is.True, "a plane across two points: one side in several pieces, the other in one");
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// The cost of taking the counts, on fixed shapes (a measurement, printed; its only assertions are that the counts are
        /// right): the cut input gate with the count and without, and a cut through the storage with each side's count and
        /// without, medians over repeated runs in turn, with the count's own time beside the cut's.
        /// </summary>
        [Test]
        public void Cost_OfTheCounts_AtTheGate_AndAtACut_OnFixedShapes()
        {
            var shapes = new[]
            {
                From("box 16 (2,700 tri)", SyntheticGeometry.Box(15, new float3(1, 1, 1), float3.zero), Smooth),
                From("sphere 64x32 seamed (4,000 tri)", SyntheticGeometry.UvSphere(64, 32, 0.5f, new float3(0.5f, 0.5f, 0.5f)), Seams),
                From("sphere 128x64 seamed (16,000 tri)", SyntheticGeometry.UvSphere(128, 64, 0.5f, new float3(0.5f, 0.5f, 0.5f)), Seams),
                From("torus 96x48 (9,200 tri)", SyntheticGeometry.Torus(96, 48, 1f, 0.3f, float3.zero), Smooth),
                From("bar field 16 (several pieces)", SyntheticGeometry.BarField(16, 3, 0.5f, 1f, float3.zero), Smooth),
            };
            const int rounds = 15;
            var text = new System.Text.StringBuilder();
            text.Append("shape | triangles | topology vertices | gate ms off | gate ms on | cut ms off | cut ms on | count ms (its own tally) | count share of the cut\n");
            double ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            foreach (Input shape in shapes)
            {
                float4 plane = SyntheticGeometry.Plane(math.normalize(new float3(0.37f, 0.61f, -0.7f)), new float3(0.31f, 0.27f, 0.36f));
                var gateOff = new List<double>(); var gateOn = new List<double>(); var cutOff = new List<double>(); var cutOn = new List<double>(); var counts = new List<double>();
                for (int r = 0; r < rounds + 2; r++)
                {
                    foreach (bool on in new[] { r % 2 == 0, r % 2 != 0 })
                    {
                        VpTopologyComponents.Enabled = on;
                        long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        VpCutInputVerdict verdict = VpCutInputGate.Check(shape.Vertices, shape.Indices, shape.TopologyOfVertex, shape.TopologyVertexCount, shape.Submeshes);
                        double gate = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * ticksToMs;
                        Assert.That(verdict.Accepted, Is.True);
                        using (VpCpuGeometryStorage storage = new VpCpuGeometryStorage(131072, 524288, 16, 64, 64, Allocator.Persistent))
                        {
                            VpStoredGeometry geometry = Append(storage, shape);
                            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True);
                            using (input)
                            {
                                double before = VpTopologyComponents.Seconds;
                                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                Assert.That(VpStorageCut.TryExecute(storage, input, plane, out VpStorageCutResult result), Is.True);
                                double cut = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * ticksToMs;
                                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, shape.Name + ": the layout -- the plane cuts the shape");
                                if (r < 2) continue;   // the first rounds warm the code
                                if (on)
                                {
                                    gateOn.Add(gate); cutOn.Add(cut); counts.Add((VpTopologyComponents.Seconds - before) * 1000.0);
                                    Assert.That(storage.TopologyComponentsOf(result.positive.geometry), Is.EqualTo(ComponentsByFlood(storage, result.positive.geometry)));
                                }
                                else
                                {
                                    gateOff.Add(gate); cutOff.Add(cut);
                                    Assert.That(storage.TopologyComponentsOf(result.positive.geometry), Is.EqualTo(VpTopologyComponents.Unknown));
                                }
                            }
                        }
                    }
                }

                double count = Median(counts), cutWith = Median(cutOn);
                text.Append(shape.Name).Append(" | ").Append(shape.Indices.Length / 3).Append(" | ").Append(shape.TopologyVertexCount)
                    .Append(" | ").Append(Median(gateOff).ToString("F4")).Append(" | ").Append(Median(gateOn).ToString("F4"))
                    .Append(" | ").Append(Median(cutOff).ToString("F4")).Append(" | ").Append(cutWith.ToString("F4"))
                    .Append(" | ").Append(count.ToString("F4")).Append(" | ").Append((100.0 * count / cutWith).ToString("F1")).Append(" %\n");
            }

            TestContext.Out.WriteLine("COMPONENT COUNT COST (medians of " + rounds + " runs each, in turn; ms):\n" + text);
        }

        [Test]
        public void ACount_IsItsOwnGeometrys_WhenAHandleIsUsedAgain()
        {
            Input box = From("closed box", SyntheticGeometry.Box(1, new float3(1, 1, 1), float3.zero), Smooth);
            Input bars = From("three closed bars", SyntheticGeometry.BarField(3, 1, 0.5f, 1f, float3.zero), Smooth);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, box);
                Assert.That(storage.TopologyComponentsOf(first), Is.EqualTo(1));

                // The box is let go and its range collected; geometries are appended until one takes the same descriptor.
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True);
                // (Until its descriptor is registered again the record is still that geometry's own, so a count read through the
                // old handle is the old geometry's or none -- never another's.)
                Assert.That(storage.TopologyComponentsOf(first), Is.EqualTo(1).Or.EqualTo(VpTopologyComponents.Unknown));
                VpStoredGeometry reused = default;
                bool sameDescriptor = false;
                for (int k = 0; k < 64 && !sameDescriptor; k++)
                {
                    reused = Append(storage, bars);
                    sameDescriptor = reused.indexRange.descriptor == first.indexRange.descriptor;
                }

                Assert.That(sameDescriptor, Is.True, "the layout: the descriptor was used again");
                Assert.That(reused.indexRange.generation, Is.Not.EqualTo(first.indexRange.generation), "under another generation");
                Assert.That(storage.TopologyComponentsOf(reused), Is.EqualTo(3), "the new geometry's own count");
                Assert.That(storage.TopologyComponentsOf(first), Is.EqualTo(VpTopologyComponents.Unknown), "and never given for the old handle");
                Assert.That(storage.TrySetTopologyComponents(first, 7), Is.False, "nor set through it");
                Assert.That(storage.TopologyComponentsOf(reused), Is.EqualTo(3));
            }
        }
    }
}
