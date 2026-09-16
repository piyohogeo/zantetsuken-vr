using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Cutting once what a Stage 3 indexed indirect display is drawing, with the GPU buffers mirroring the storage: a
    /// crossing cut transfers the appended vertices once and the two sides' one contiguous run once, a cut that misses
    /// transfers nothing at all, a shortage refuses before any transfer begins, a frame draws only what its own
    /// BeginFrame settled, and a transform held alongside a cut that changed nothing is still applied.
    /// <para>
    /// The frame counter is driven from here, through the display's test entry point, because an EditMode test has no
    /// frame loop to advance. <see cref="BeginNextFrame"/> is a new frame; <see cref="BeginSameFrame"/> is the same one
    /// again, which the display must ignore.
    /// </para>
    /// </summary>
    public class VpIndirectCutDisplayTests
    {
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private int _frame;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
        }

        [TearDown]
        public void DestroyObjects()
        {
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        /// <summary>The engine has moved on a frame, and the display opens it.</summary>
        private void BeginNextFrame(VpIndirectCutDisplay display)
        {
            _frame++;
            display.BeginFrame();
        }

        /// <summary>The same frame again: the display must treat this as nothing at all.</summary>
        private static void BeginSameFrame(VpIndirectCutDisplay display)
        {
            display.BeginFrame();
        }

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private sealed class Prepared
        {
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public VpGeometrySubmesh[] Submeshes;
        }

        private static Prepared BuildPrepared()
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = k_controlPoints[c[k]], normal = n, uv0 = uv[k] });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            return new Prepared
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                TopologyOfVertex = topology.ToArray(),
                Submeshes = submeshes.ToArray(),
            };
        }

        private static VpCpuGeometryStorage NewStorage(int indexCapacity = 8192)
        {
            return new VpCpuGeometryStorage(2048, indexCapacity, 32, 128, 128, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, ControlPoints, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        private static float4 CrossingPlane()
        {
            float3 centre = float3.zero;
            foreach (float3 p in k_controlPoints)
            {
                centre += p;
            }

            centre /= k_controlPoints.Length;
            float3 n = math.normalize(new float3(0.37f, 0.61f, -0.7f));
            return new float4(n, -math.dot(n, centre + new float3(0.0071f, -0.0233f, 0.0119f)));
        }

        /// <summary>A plane the shape lies entirely on one side of: it spans y in [0, 1.3].</summary>
        private static float4 MissingPlane(bool positiveSide)
        {
            float3 n = new float3(0, 1, 0);
            float3 point = new float3(0, positiveSide ? -3f : 3f, 0);
            return new float4(n, -math.dot(n, point));
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return new Dictionary<int, Material>
            {
                { SideMaterial, Track(new Material(shader) { name = "side" }) },
                { EndMaterial, Track(new Material(shader) { name = "end" }) },
            };
        }

        private static (int start, int count) IndexPlace(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out _, out int start, out int count), Is.True, "index state");
            return (start, count);
        }

        private static VpIndexRangeState IndexState(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _), Is.True, "index state");
            return state;
        }

        private bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            VpStoredGeometry geometry,
            out VpIndirectCutDisplay display,
            Matrix4x4? objectToWorld = null,
            int commandCapacity = 16,
            int instanceCapacity = 16)
        {
            return VpIndirectCutDisplay.TryCreate(
                storage,
                table,
                geometry,
                Materials(),
                null,
                objectToWorld ?? Matrix4x4.identity,
                commandCapacity,
                instanceCapacity,
                () => _frame,
                out display);
        }

        [Test]
        public void TheFirstUpload_PutsTheCommandsWhereTheStorageHoldsTheIndices()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);

                // A geometry before it, so the one shown does not begin at position 0.
                Append(storage, prepared);
                VpStoredGeometry parent = Append(storage, prepared);
                (int start, int count) = IndexPlace(storage, parent);
                Assert.That(start, Is.GreaterThan(0), "the shown geometry lies past another");

                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.VertexTransfers, Is.EqualTo(1), "the committed vertices went across once");
                    Assert.That(display.IndexTransfers, Is.EqualTo(1), "and the geometry's indices once");
                    Assert.That(display.CommandUploads, Is.EqualTo(1));
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                    Assert.That(display.GetShownCommandCount(0), Is.EqualTo(2), "one command per submesh");

                    int covered = 0;
                    for (int c = 0; c < display.GetShownCommandCount(0); c++)
                    {
                        VpIndirectCommand command = display.GetShownCommand(0, c);
                        Assert.That(command.range.indexStart, Is.EqualTo(start + covered), "command " + c + " starts where the storage holds it");
                        Assert.That(command.instanceCount, Is.EqualTo(1));
                        covered += command.range.indexCount;
                    }

                    Assert.That(covered, Is.EqualTo(count), "the commands cover the published range exactly");
                }
            }
        }

        [Test]
        public void ACrossingCut_TransfersTheAppendedVerticesAndOneIndexRun()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    int verticesBefore = storage.VertexCount;
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True, "the request is held");
                    Assert.That(display.HasPendingCut, Is.True, "and not applied yet");
                    Assert.That(display.IndexTransfers, Is.EqualTo(1), "nothing was transferred by the request itself");

                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;
                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));
                    Assert.That(result.cut.kernel.crossingTriangles, Is.GreaterThan(0), "the plane really cuts");
                    Assert.That(display.ShownCount, Is.EqualTo(2), "both sides are shown");

                    // exactly one transfer of each kind, covering exactly what changed
                    Assert.That(result.vertexTransfers, Is.EqualTo(1), "one vertex transfer");
                    Assert.That(result.indexTransfers, Is.EqualTo(1), "one index transfer");
                    Assert.That(result.transferredVertices, Is.EqualTo(storage.VertexCount - verticesBefore), "only the appended vertices");

                    (int positiveStart, int positiveCount) = IndexPlace(storage, result.cut.positive.geometry);
                    (int negativeStart, int negativeCount) = IndexPlace(storage, result.cut.negative.geometry);
                    Assert.That(negativeStart, Is.EqualTo(positiveStart + positiveCount), "the two sides are one contiguous run");
                    Assert.That(result.transferredIndices, Is.EqualTo(positiveCount + negativeCount), "only the run they use");

                    // the commands address the storage's own positions
                    Assert.That(display.GetShownCommand(0, 0).range.indexStart, Is.EqualTo(positiveStart), "the positive side");
                    Assert.That(display.GetShownCommand(1, 0).range.indexStart, Is.EqualTo(negativeStart), "the negative side");
                    Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Free), "the parent's range was retired");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(2), "only the children are registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2));
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ACutThatMisses_TransfersNothingAtAll(bool positiveSide)
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;

                    Assert.That(display.TryRequestCut(MissingPlane(positiveSide)), Is.True);
                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;

                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.KeptParent));
                    Assert.That(result.indexTransfers, Is.Zero, "no index transfer for a cut that reuses its input");
                    Assert.That(result.vertexTransfers, Is.Zero, "and no vertex transfer");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "the display's count is unchanged");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers));
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "and the commands were not rewritten");
                    Assert.That(display.ShownCount, Is.EqualTo(1), "the parent is still the one shown");
                    Assert.That(display.GetShownGeometry(0).indexRange, Is.EqualTo(parent.indexRange));
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1), "nothing new was registered");
                }
            }
        }

        [Test]
        public void TheChildren_KeepTheSubmeshesTheMaterialsAndTheTransform()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                var placement = Matrix4x4.TRS(new Vector3(3f, -2f, 5f), Quaternion.Euler(20f, 40f, 60f), new Vector3(2f, 0.5f, 1.5f));
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display, placement), Is.True, "create");
                using (display)
                {
                    Material side = display.GetShownCommandMaterial(0, 0);
                    Material end = display.GetShownCommandMaterial(0, 1);
                    Assert.That(side, Is.Not.SameAs(end), "the two submeshes have their own materials");

                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));

                    for (int i = 0; i < display.ShownCount; i++)
                    {
                        Assert.That(display.GetShownCommandCount(i), Is.EqualTo(2), "child " + i + " keeps both submeshes");
                        Assert.That(display.GetShownCommandMaterial(i, 0), Is.SameAs(side), "child " + i + " submesh 0 material");
                        Assert.That(display.GetShownCommandMaterial(i, 1), Is.SameAs(end), "child " + i + " submesh 1 material");
                        Assert.That(display.GetShownTransform(i), Is.EqualTo(placement), "child " + i + " keeps the placement");
                    }
                }
            }
        }

        [Test]
        public void AShortageOfCommandRoom_BeginsNoTransfer()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);

                // Room for the parent's two commands, but not for the four its two sides would need.
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display, null, 2, 2), Is.True, "create");
                using (display)
                {
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;

                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;

                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.DisplayPreparationFailed));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut itself succeeded");
                    Assert.That(result.vertexTransfers, Is.Zero, "no transfer was begun");
                    Assert.That(result.indexTransfers, Is.Zero);
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "the display's counts are unchanged");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads));

                    // the parent is untouched and both published sides were given back
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                    Assert.That(display.GetShownGeometry(0).indexRange, Is.EqualTo(parent.indexRange));
                    Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "the parent's range");
                    Assert.That(IndexState(storage, result.cut.positive.geometry), Is.EqualTo(VpIndexRangeState.Free), "the positive side");
                    Assert.That(IndexState(storage, result.cut.negative.geometry), Is.EqualTo(VpIndexRangeState.Free), "the negative side");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1), "only the parent is registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1));
                    Assert.That(result.appendedVerticesKept, Is.GreaterThan(0), "the appended vertices stay, and are reported");
                }
            }
        }

        /// <summary>
        /// A frame is identified, not merely begun: once it has drawn, calling BeginFrame again within it does nothing,
        /// and the request waits for a frame that really is the next one.
        /// </summary>
        [Test]
        public void ARequestMadeAfterTheFrameHasDrawn_WaitsForARealNextFrame()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    BeginNextFrame(display);
                    display.Render(0);
                    Assert.That(display.HasDrawnThisFrame, Is.True, "the frame has registered a draw");

                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True, "the request is taken");

                    // the same frame again: nothing is applied and the frame stays closed
                    BeginSameFrame(display);
                    Assert.That(display.HasPendingCut, Is.True, "the request is still waiting");
                    Assert.That(display.HasDrawnThisFrame, Is.True, "the frame is not reopened");
                    Assert.That(display.ShownCount, Is.EqualTo(1), "the frame still shows what it drew");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "no vertex went across");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "no index went across");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "and the commands were not rewritten");
                    display.Render(0);
                    Assert.That(display.ShownCount, Is.EqualTo(1), "a second camera of the same frame draws the same data");

                    // a real next frame applies it
                    BeginNextFrame(display);
                    Assert.That(display.HasPendingCut, Is.False);
                    Assert.That(display.HasDrawnThisFrame, Is.False, "the new frame has not drawn yet");
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));
                    Assert.That(display.ShownCount, Is.EqualTo(2));
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers + 1), "one index transfer, in the new frame");
                }
            }
        }

        /// <summary>
        /// A frame draws only what its own BeginFrame settled. A display fresh from creation has opened no frame at
        /// all, so it draws nothing, and a request made then cannot slip into a frame that has already drawn.
        /// </summary>
        [Test]
        public void ADrawBeforeTheFrameIsOpened_IsRefused()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    int commandUploads = display.CommandUploads;
                    Assert.Throws<InvalidOperationException>(() => display.Render(0), "no frame has been opened");
                    Assert.That(display.HasDrawnThisFrame, Is.False, "and nothing was registered");

                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    Assert.Throws<InvalidOperationException>(() => display.Render(0), "still no frame");
                    Assert.That(display.HasPendingCut, Is.True, "the request is untouched");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "and nothing was updated");

                    // opening the frame is what lets it draw, and it applies what was held
                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));
                    display.Render(0);
                    Assert.That(display.HasDrawnThisFrame, Is.True);
                }
            }
        }

        /// <summary>Being open for the frame before is not being open for this one.</summary>
        [Test]
        public void AFrameOpenedEarlier_DoesNotLetTheNextFrameDraw()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    BeginNextFrame(display);
                    display.Render(0);

                    // the engine moves on, but nobody opened the new frame
                    _frame++;
                    Assert.Throws<InvalidOperationException>(() => display.Render(0), "the open frame is not this one");

                    display.BeginFrame();
                    display.Render(0);
                    Assert.That(display.HasDrawnThisFrame, Is.True, "opening it is what lets it draw");
                }
            }
        }

        [Test]
        public void ATransformChange_TakesEffectAtTheNextFrameOnly()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    BeginNextFrame(display);
                    display.Render(0);
                    int commandUploads = display.CommandUploads;

                    var moved = Matrix4x4.Translate(new Vector3(0f, 4f, 0f));
                    Assert.That(display.TrySetTransform(0, moved), Is.True, "the transform is recorded");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "but nothing was uploaded in the drawn frame");

                    BeginSameFrame(display);
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "nor by reopening the same frame");
                    Assert.That(display.HasPendingTransform, Is.True, "it is still waiting");

                    BeginNextFrame(display);
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads + 1), "the new frame uploads the instances");
                    Assert.That(display.HasPendingTransform, Is.False);
                    Assert.That(display.GetShownTransform(0), Is.EqualTo(moved));
                }
            }
        }

        /// <summary>
        /// A cut that reuses its input rewrites nothing, so a transform held in the same frame would be lost if the cut
        /// were taken to have carried it. It has to be applied on its own — and still without any vertex or index
        /// transfer, which is what the non-crossing case costs.
        /// </summary>
        [Test]
        public void ATransformHeldWithACutThatMisses_IsStillApplied()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;

                    var moved = Matrix4x4.Translate(new Vector3(0f, 4f, 0f));
                    Assert.That(display.TrySetTransform(0, moved), Is.True);
                    Assert.That(display.TryRequestCut(MissingPlane(true)), Is.True);

                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.KeptParent));
                    Assert.That(display.HasPendingTransform, Is.False, "the transform was applied");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads + 1), "through one command and instance upload");
                    Assert.That(display.GetShownTransform(0), Is.EqualTo(moved));
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "with no vertex transfer");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "and no index transfer");
                }
            }
        }

        /// <summary>The same when the cut is refused outright, or only paused for want of reservation.</summary>
        [TestCase("refused")]
        [TestCase("retry")]
        public void ATransformHeldWithACutThatFails_IsStillApplied(string kind)
        {
            Prepared prepared = BuildPrepared();
            bool refused = kind == "refused";

            // "refused": an index capacity with no room for the cut's reservation at all.
            using (VpCpuGeometryStorage storage = refused ? NewStorage(prepared.Indices.Length + 8) : NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;

                    var moved = Matrix4x4.Translate(new Vector3(0f, 4f, 0f));
                    Assert.That(display.TrySetTransform(0, moved), Is.True);
                    var options = refused ? default : new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3, maxAttempts = 1 };
                    Assert.That(display.TryRequestCut(CrossingPlane(), options), Is.True);

                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;
                    Assert.That(
                        result.outcome,
                        Is.EqualTo(refused ? VpIndirectCutOutcome.CutRefused : VpIndirectCutOutcome.CapacityRetry),
                        "the cut " + kind);
                    Assert.That(display.ShownCount, Is.EqualTo(1), "the parent is still shown");

                    Assert.That(display.HasPendingTransform, Is.False, "the transform was applied all the same");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads + 1), "through one command and instance upload");
                    Assert.That(display.GetShownTransform(0), Is.EqualTo(moved));
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "with no vertex transfer");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "and no index transfer");
                }
            }
        }

        [Test]
        public void EverythingIsGivenBackOnce_AfterASwapAndOnDispose()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");

                Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                BeginNextFrame(display);
                Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));
                VpStoredGeometry first = display.GetShownGeometry(0);
                VpStoredGeometry second = display.GetShownGeometry(1);
                Assert.That(storage.TryRetireIndices(parent.indexRange), Is.False, "the parent's range is already retired");

                display.Dispose();
                Assert.That(display.IsDisposed, Is.True);
                Assert.That(table.LiveGeometryCount, Is.Zero, "every registration is back");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "every instance is back");
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.False, "the first child's range is already retired");
                Assert.That(storage.TryRetireIndices(second.indexRange), Is.False, "and the second child's");

                display.Dispose();
                Assert.That(table.LiveGeometryCount, Is.Zero, "disposing again gives nothing back twice");
                Assert.Throws<ObjectDisposedException>(() => display.BeginFrame(), "no frame after disposal");
                Assert.Throws<ObjectDisposedException>(() => display.Render(0), "no draw after disposal");
                Assert.Throws<ObjectDisposedException>(() => display.TryRequestCut(CrossingPlane()), "and no request");
            }
        }

        [Test]
        public void ACreationThatCannotBeShown_TakesNothing()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);

                var incomplete = new Dictionary<int, Material> { { SideMaterial, Materials()[SideMaterial] } };
                Assert.That(
                    VpIndirectCutDisplay.TryCreate(storage, table, parent, incomplete, null, Matrix4x4.identity, 16, 16, out VpIndirectCutDisplay refused),
                    Is.False,
                    "a material its submeshes name is missing");
                Assert.That(refused, Is.Null);
                Assert.That(table.LiveGeometryCount, Is.Zero, "nothing was registered");
                Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "and the geometry is untouched");

                // no room for the commands it would need
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay tooSmall, null, 1, 1), Is.False, "no command room");
                Assert.That(tooSmall, Is.Null);
                Assert.That(table.LiveGeometryCount, Is.Zero, "still nothing registered");
                Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published));

                Assert.That(TryCreate(null, table, parent, out _), Is.False, "a null storage");
                Assert.That(TryCreate(storage, null, parent, out _), Is.False, "a null table");

                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay good), Is.True, "a complete request still works");
                good.Dispose();
            }
        }

        /// <summary>
        /// The table refusing the registration is the last thing that can go wrong, and by then the transfers have been
        /// made. It must still leave the geometry the caller handed in exactly as it was: registered nowhere, published
        /// still, and its retirement nobody else's.
        /// </summary>
        [Test]
        public void ACreationTheTableRefuses_LeavesTheGeometryPublished()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                // room for one registration but for no display instance at all
                var table = new VpGeometryReferenceTable(storage, 4, 0);
                VpStoredGeometry parent = Append(storage, prepared);

                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.False, "no room to show it");
                Assert.That(display, Is.Null);
                Assert.That(table.LiveGeometryCount, Is.Zero, "nothing was registered");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero);
                Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "the geometry is untouched");
                Assert.That(storage.TryRetireIndices(parent.indexRange), Is.True, "and its retirement is still the caller's to make");
            }
        }
    }
}
