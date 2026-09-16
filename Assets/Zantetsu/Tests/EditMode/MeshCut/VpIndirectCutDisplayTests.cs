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

        /// <summary>
        /// The plane the storage tests cut a child with: it crosses the positive side of <see cref="CrossingPlane"/>
        /// over this same shape, so the second cut really is a cut.
        /// </summary>
        private static float4 ChildPlane()
        {
            float3 n = math.normalize(new float3(0.81f, -0.23f, 0.54f));
            return new float4(n, -math.dot(n, new float3(0.0313f, 0.6217f, -0.0119f)));
        }

        /// <summary>
        /// Everything about one shown geometry that a cut of a different one must not disturb — the geometry, its
        /// placement, its commands and materials, and **the indices themselves**, read out under the geometry's own
        /// lease. Comparing the handles and the command ranges alone would not notice the index list being rewritten
        /// under the same range, which is exactly what "the sibling is untouched" has to mean.
        /// <para>
        /// The copy this takes is the test's own. It says nothing about the product path, which transfers the storage's
        /// memory where it lies and makes no array of its own.
        /// </para>
        /// </summary>
        private sealed class ShownSnapshot
        {
            public VpStoredGeometry geometry;
            public Matrix4x4 objectToWorld;
            public VpIndirectCommand[] commands;
            public Material[] materials;
            public uint[] indices;

            public static ShownSnapshot Of(VpCpuGeometryStorage storage, VpIndirectCutDisplay display, int index)
            {
                var commands = new VpIndirectCommand[display.GetShownCommandCount(index)];
                var materials = new Material[commands.Length];
                for (int c = 0; c < commands.Length; c++)
                {
                    commands[c] = display.GetShownCommand(index, c);
                    materials[c] = display.GetShownCommandMaterial(index, c);
                }

                VpStoredGeometry geometry = display.GetShownGeometry(index);
                return new ShownSnapshot
                {
                    geometry = geometry,
                    objectToWorld = display.GetShownTransform(index),
                    commands = commands,
                    materials = materials,
                    indices = ReadIndices(storage, geometry),
                };
            }

            public void AssertUnchangedAt(VpCpuGeometryStorage storage, VpIndirectCutDisplay display, int index, string label)
            {
                Assert.That(display.GetShownGeometry(index).indexRange, Is.EqualTo(geometry.indexRange), label + ": geometry");
                Assert.That(display.GetShownTransform(index), Is.EqualTo(objectToWorld), label + ": transform");
                Assert.That(display.GetShownCommandCount(index), Is.EqualTo(commands.Length), label + ": command count");
                for (int c = 0; c < commands.Length; c++)
                {
                    VpIndirectCommand now = display.GetShownCommand(index, c);
                    Assert.That(now.range.indexStart, Is.EqualTo(commands[c].range.indexStart), label + ": command " + c + " start");
                    Assert.That(now.range.indexCount, Is.EqualTo(commands[c].range.indexCount), label + ": command " + c + " count");
                    Assert.That(display.GetShownCommandMaterial(index, c), Is.SameAs(materials[c]), label + ": command " + c + " material");
                }

                Assert.That(ReadIndices(storage, display.GetShownGeometry(index)), Is.EqualTo(indices), label + ": the index list itself");
            }

            /// <summary>The geometry's published indices, copied out under its own read lease and the lease returned.</summary>
            private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
            {
                Assert.That(
                    storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view),
                    Is.True,
                    "lease the indices");
                var copy = new uint[view.Length];
                for (int i = 0; i < copy.Length; i++)
                {
                    copy[i] = view[i];
                }

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release the lease");
                return copy;
            }
        }

        /// <summary>
        /// A plane through the middle of one shown geometry, across its widest extent, in that geometry's own
        /// coordinates. Taken from the geometry's own bounds, so it crosses it rather than missing it — which is what
        /// lets a test choose any shown geometry as the target and still be cutting something.
        /// </summary>
        private static float4 PlaneThroughShown(VpIndirectCutDisplay display, int index)
        {
            Bounds bounds = display.GetShownCommand(index, 0).localBounds;
            for (int c = 1; c < display.GetShownCommandCount(index); c++)
            {
                bounds.Encapsulate(display.GetShownCommand(index, c).localBounds);
            }

            Vector3 size = bounds.size;
            float3 axis = size.x >= size.y && size.x >= size.z
                ? new float3(1, 0, 0)
                : size.y >= size.z ? new float3(0, 1, 0) : new float3(0, 0, 1);

            // Just off the middle, so that no vertex sits exactly on the plane.
            float3 point = (float3)bounds.center + axis * (0.0137f * math.max(0.001f, math.length((float3)size) * 0.25f));
            return new float4(axis, -math.dot(axis, point));
        }

        /// <summary>
        /// The whole scenario: one geometry becomes two, and then the chosen one of those two becomes two more, leaving
        /// three on screen. The sibling nobody chose is not touched by any of it — same geometry, same indices, same
        /// materials, same placement, same registration.
        /// </summary>
        [Test]
        public void ASelectedChild_IsCutAgainWhileItsSiblingIsKept()
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
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True, "the first request");
                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped), "the first cut");
                    Assert.That(display.ShownCount, Is.EqualTo(2), "two after the first");

                    VpStoredGeometry target = display.GetShownGeometry(0);
                    ShownSnapshot sibling = ShownSnapshot.Of(storage, display, 1);
                    int verticesBefore = storage.VertexCount;

                    // the request names the geometry, and that is what comes back
                    Assert.That(display.TryRequestCut(target, ChildPlane()), Is.True, "the second request");
                    Assert.That(display.TryGetPendingTarget(out VpStoredGeometry pending), Is.True, "a target is waiting");
                    Assert.That(pending.indexRange, Is.EqualTo(target.indexRange), "and it is the geometry that was asked for");

                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;
                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped), "the second cut");
                    Assert.That(result.cut.kernel.crossingTriangles, Is.GreaterThan(0), "the second plane really cuts");
                    Assert.That(display.ShownCount, Is.EqualTo(3), "the sibling and the two new sides");

                    // the two new sides took the target's place, in its position in the list
                    (int firstStart, int firstCount) = IndexPlace(storage, result.cut.positive.geometry);
                    (int secondStart, int secondCount) = IndexPlace(storage, result.cut.negative.geometry);
                    Assert.That(display.GetShownGeometry(0).indexRange, Is.EqualTo(result.cut.positive.geometry.indexRange), "the positive grandchild");
                    Assert.That(display.GetShownGeometry(1).indexRange, Is.EqualTo(result.cut.negative.geometry.indexRange), "the negative grandchild");
                    Assert.That(display.GetShownCommand(0, 0).range.indexStart, Is.EqualTo(firstStart), "its commands address the storage's own position");
                    Assert.That(display.GetShownCommand(1, 0).range.indexStart, Is.EqualTo(secondStart));
                    Assert.That(display.GetShownTransform(0), Is.EqualTo(placement), "and they inherit what they were cut from");
                    Assert.That(display.GetShownTransform(1), Is.EqualTo(placement));

                    // the sibling nobody chose is exactly as it was, and still registered
                    sibling.AssertUnchangedAt(storage, display, 2, "the sibling");
                    Assert.That(IndexState(storage, sibling.geometry), Is.EqualTo(VpIndexRangeState.Published), "the sibling's range");
                    Assert.That(IndexState(storage, target), Is.EqualTo(VpIndexRangeState.Free), "only the target was retired");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(3), "the sibling and the two new sides");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(3));

                    // the transfers are the cut's own, and no more
                    Assert.That(result.vertexTransfers, Is.EqualTo(1), "one vertex transfer");
                    Assert.That(result.transferredVertices, Is.EqualTo(storage.VertexCount - verticesBefore), "of the appended vertices only");
                    Assert.That(result.indexTransfers, Is.EqualTo(1), "one index transfer");
                    Assert.That(secondStart, Is.EqualTo(firstStart + firstCount), "the two sides are one contiguous run");
                    Assert.That(result.transferredIndices, Is.EqualTo(firstCount + secondCount), "covering exactly that run");
                }
            }
        }

        /// <summary>A geometry the display does not show — the retired parent, say — is not a target.</summary>
        [Test]
        public void ARequestForAGeometryThatIsNotShown_IsRefused()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.IndexOfShown(parent), Is.Zero, "the parent is what is shown");
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.ShownCount, Is.EqualTo(2));

                    Assert.That(display.IndexOfShown(parent), Is.EqualTo(-1), "the parent is no longer shown");
                    Assert.That(display.TryRequestCut(parent, ChildPlane()), Is.False, "so it cannot be the target");
                    Assert.That(display.HasPendingCut, Is.False, "and nothing is waiting");
                    Assert.That(display.TryGetPendingTarget(out VpStoredGeometry none), Is.False);
                    Assert.That(none, Is.EqualTo(default(VpStoredGeometry)));

                    // the plain request needs exactly one shown geometry, and there are two
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.False, "no target to infer");
                }
            }
        }

        /// <summary>A second cut that misses costs nothing: the child is lent back and the display is untouched.</summary>
        [Test]
        public void ACutOfAChildThatMisses_TransfersNothing()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.ShownCount, Is.EqualTo(2));

                    ShownSnapshot first = ShownSnapshot.Of(storage, display, 0);
                    ShownSnapshot second = ShownSnapshot.Of(storage, display, 1);
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;
                    int liveGeometries = table.LiveGeometryCount;

                    Assert.That(display.TryRequestCut(display.GetShownGeometry(0), MissingPlane(true)), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.KeptParent));

                    Assert.That(display.ShownCount, Is.EqualTo(2), "still the two children");
                    first.AssertUnchangedAt(storage, display, 0, "the target");
                    second.AssertUnchangedAt(storage, display, 1, "the sibling");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "no vertex transfer");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "no index transfer");
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "and no command upload");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(liveGeometries), "nothing registered or retired");
                }
            }
        }

        /// <summary>
        /// A second cut that will not fit refuses before any transfer, and both children stay on screen with everything
        /// they had. The sides it had published are given back.
        /// </summary>
        [Test]
        public void AShortageAtTheSecondCut_KeepsBothChildren()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);

                // Room for the two children's four commands, but not for the six three geometries would need.
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display, null, 4, 4), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.ShownCount, Is.EqualTo(2), "the first cut fits");

                    ShownSnapshot first = ShownSnapshot.Of(storage, display, 0);
                    ShownSnapshot second = ShownSnapshot.Of(storage, display, 1);
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int commandUploads = display.CommandUploads;

                    Assert.That(display.TryRequestCut(display.GetShownGeometry(0), ChildPlane()), Is.True);
                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;

                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.DisplayPreparationFailed));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut itself succeeded");
                    Assert.That(result.vertexTransfers, Is.Zero, "no transfer was begun");
                    Assert.That(result.indexTransfers, Is.Zero);
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "the display's counts are unchanged");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));
                    Assert.That(display.CommandUploads, Is.EqualTo(commandUploads));

                    Assert.That(display.ShownCount, Is.EqualTo(2), "both children are still shown");
                    first.AssertUnchangedAt(storage, display, 0, "the target");
                    second.AssertUnchangedAt(storage, display, 1, "the sibling");
                    Assert.That(IndexState(storage, first.geometry), Is.EqualTo(VpIndexRangeState.Published), "the target's range");
                    Assert.That(IndexState(storage, result.cut.positive.geometry), Is.EqualTo(VpIndexRangeState.Free), "the positive side was given back");
                    Assert.That(IndexState(storage, result.cut.negative.geometry), Is.EqualTo(VpIndexRangeState.Free), "and the negative");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(2), "only the two children are registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2));
                    Assert.That(result.appendedVerticesKept, Is.GreaterThan(0), "the appended vertices stay, and are reported");
                }
            }
        }

        /// <summary>Three shown geometries are given back once each, and the ranges they held are retired once.</summary>
        [Test]
        public void AfterTwoCuts_EverythingIsGivenBackOnce()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");

                Assert.That(display.TryRequestCut(CrossingPlane()), Is.True);
                BeginNextFrame(display);
                Assert.That(display.TryRequestCut(display.GetShownGeometry(0), ChildPlane()), Is.True);
                BeginNextFrame(display);
                Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped));
                Assert.That(display.ShownCount, Is.EqualTo(3));

                var shown = new VpStoredGeometry[3];
                for (int i = 0; i < 3; i++)
                {
                    shown[i] = display.GetShownGeometry(i);
                }

                Assert.That(table.LiveGeometryCount, Is.EqualTo(3));
                display.Dispose();
                Assert.That(table.LiveGeometryCount, Is.Zero, "every registration is back");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "every instance is back");
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(storage.TryRetireIndices(shown[i].indexRange), Is.False, "shown " + i + " was already retired");
                }

                display.Dispose();
                Assert.That(table.LiveGeometryCount, Is.Zero, "disposing again gives nothing back twice");
            }
        }

        /// <summary>
        /// The target is whichever geometry was named, not the first one, and the sides take **its** placement, not the
        /// first one's. The two are given different transforms first, so an implementation that always cut the head of
        /// the list, or always inherited the head's transform, could not pass this.
        /// </summary>
        [Test]
        public void ATargetThatIsNotTheFirst_IsCutAndItsOwnTransformIsInherited()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(TryCreate(storage, table, parent, out VpIndirectCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryRequestCut(CrossingPlane()), Is.True, "the first request");
                    BeginNextFrame(display);
                    Assert.That(display.LastCutResult.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped), "the first cut");
                    Assert.That(display.ShownCount, Is.EqualTo(2));

                    // the two are placed differently, and the frame after that is where it takes effect
                    var firstPlacement = Matrix4x4.TRS(new Vector3(-4f, 1f, 2f), Quaternion.Euler(10f, 0f, 0f), Vector3.one);
                    var targetPlacement = Matrix4x4.TRS(new Vector3(6f, -3f, 1f), Quaternion.Euler(0f, 75f, 15f), new Vector3(1.5f, 2f, 0.5f));
                    Assert.That(firstPlacement, Is.Not.EqualTo(targetPlacement), "the two placements differ");
                    Assert.That(display.TrySetTransform(0, firstPlacement), Is.True);
                    Assert.That(display.TrySetTransform(1, targetPlacement), Is.True);
                    BeginNextFrame(display);
                    Assert.That(display.HasPendingTransform, Is.False, "both placements were applied");
                    Assert.That(display.GetShownTransform(0), Is.EqualTo(firstPlacement));
                    Assert.That(display.GetShownTransform(1), Is.EqualTo(targetPlacement));

                    // the second geometry is the target, and the plane comes from its own extent so that it crosses it
                    VpStoredGeometry target = display.GetShownGeometry(1);
                    ShownSnapshot sibling = ShownSnapshot.Of(storage, display, 0);
                    float4 plane = PlaneThroughShown(display, 1);
                    Assert.That(display.TryRequestCut(target, plane), Is.True, "the second request");
                    Assert.That(display.TryGetPendingTarget(out VpStoredGeometry pending), Is.True);
                    Assert.That(pending.indexRange, Is.EqualTo(target.indexRange), "the target is the one that was named");

                    BeginNextFrame(display);
                    VpIndirectCutResult result = display.LastCutResult;
                    Assert.That(result.outcome, Is.EqualTo(VpIndirectCutOutcome.Swapped), "the second cut");
                    Assert.That(result.cut.kernel.crossingTriangles, Is.GreaterThan(0), "the plane really crosses the target");
                    Assert.That(display.ShownCount, Is.EqualTo(3));

                    // the sides took the target's place in the list, and the target's placement with it
                    Assert.That(display.GetShownGeometry(1).indexRange, Is.EqualTo(result.cut.positive.geometry.indexRange), "the positive side");
                    Assert.That(display.GetShownGeometry(2).indexRange, Is.EqualTo(result.cut.negative.geometry.indexRange), "the negative side");
                    Assert.That(display.GetShownTransform(1), Is.EqualTo(targetPlacement), "the positive side inherits the target's placement");
                    Assert.That(display.GetShownTransform(2), Is.EqualTo(targetPlacement), "and so does the negative");
                    Assert.That(display.GetShownTransform(1), Is.Not.EqualTo(firstPlacement), "not the first geometry's");

                    // and the one nobody chose is untouched, indices included
                    sibling.AssertUnchangedAt(storage, display, 0, "the sibling");
                    Assert.That(IndexState(storage, sibling.geometry), Is.EqualTo(VpIndexRangeState.Published), "the sibling's range");
                    Assert.That(IndexState(storage, target), Is.EqualTo(VpIndexRangeState.Free), "only the target was retired");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(3));
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(3));
                }
            }
        }
    }
}
