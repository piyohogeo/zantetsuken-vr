using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// DESIGN 4.5.3's vertex room by lineage through the product's own world: a body's room stays while any fragment of
    /// its lineage lives or a work still reads it, goes back through the ordinary retirements once none does, and is
    /// taken again by the next body, whose GPU copy is its own and which is cut as usual. Every case ends the world.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        /// <summary>
        /// **A lineage's room comes back when its last piece goes, and the next body takes it.** A body is cut in two
        /// beside another body that stays; one side retired leaves the room held for the other; both retired, the room
        /// goes back, once. The next body -- a box whose own vertices are offset, so its content differs from what the
        /// room held -- is given the same vertex room: the GPU holds its new vertices there, not the old ones, the body
        /// that stayed keeps its own vertices on the GPU, and the new body is cut again as usual.
        /// </summary>
        [UnityTest]
        public IEnumerator ALineageWhosePiecesAreAllRetired_GivesItsVertexRoomBack_AndTheNextBodyTakesIt()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            LogicalFragmentId stays = AddBody(root, new Vector3(4f, 0f, 0f));
            root.Lifetime.MarkLineage(body);
            yield return null;
            Assert.That(root.Geometry.TryGetGeometry(stays, out VpStoredGeometry staysGeometry), Is.True);
            Assert.That(root.Storage.TryGetCommittedVertices(staysGeometry.vertexStart, staysGeometry.vertexCount, out NativeArray<VpRenderVertex> staysCpu), Is.True);
            VpRenderVertex[] staysBefore = staysCpu.ToArray();
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            Assert.That(root.Geometry.TryGetGeometry(sides[0], out VpStoredGeometry sideGeometry), Is.True);
            Assert.That(root.Storage.TryGetVertexGroup(sideGeometry, out int group), Is.True, "the layout: the lineage's group");
            int groups = root.Storage.VertexGroupCount;
            int freeBefore = root.Storage.FreeVertexRoom;
            Assert.That(root.Storage.TryGetCommittedVertices(group, 24, out NativeArray<VpRenderVertex> oldCpu), Is.True, "the layout: the root box's 24 vertices");
            VpRenderVertex[] oldContent = oldCpu.ToArray();

            Assert.That(root.Lifetime.TryRetire(sides[0], out string refusal), Is.True, refusal);
            for (int i = 0; i < 4; i++)
            {
                yield return null;
            }

            Assert.That(root.Geometry.VertexGroupsReleased, Is.Zero, "the other side still lives: the room is held");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups));

            Assert.That(root.Lifetime.TryRetire(sides[1], out refusal), Is.True, refusal);
            yield return Until(() => root.Geometry.VertexGroupsReleased == 1, "the lineage's room went back");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups - 1));
            Assert.That(root.Storage.FreeVertexRoom, Is.GreaterThan(freeBefore));
            yield return null;
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(1), "once");

            // The next body takes the room given back, is drawn with its own vertices and is cut as usual.
            LogicalFragmentId next = AddBody(root, new Vector3(0f, 0f, 4f), false, new Vector3(0.3f, 0f, 0.2f));
            yield return null;
            yield return null;
            Assert.That(root.Geometry.TryGetGeometry(next, out VpStoredGeometry nextGeometry), Is.True);
            Assert.That(nextGeometry.vertexStart, Is.EqualTo(group), "the next body's vertices are where the lineage's were");
            Assert.That(IsDrawn(root, next), Is.True);
            VpGpuIndexedGeometryBuffers gpu = root.Display.Buffers;
            var gpuVertices = new VpRenderVertex[gpu.VertexCapacity];
            gpu.VertexBuffer.GetData(gpuVertices);
            Assert.That(root.Storage.TryGetCommittedVertices(nextGeometry.vertexStart, nextGeometry.vertexCount, out NativeArray<VpRenderVertex> cpu), Is.True);
            Assert.That(cpu.Length, Is.EqualTo(oldContent.Length), "the layout: the same room, as many vertices");
            for (int v = 0; v < cpu.Length; v++)
            {
                Assert.That(cpu[v].position, Is.Not.EqualTo(oldContent[v].position), "the layout: vertex " + v + " differs from what the room held");
                Assert.That(gpuVertices[nextGeometry.vertexStart + v].position, Is.EqualTo(cpu[v].position), "vertex " + (nextGeometry.vertexStart + v) + " on the GPU is the new body's");
                Assert.That(gpuVertices[nextGeometry.vertexStart + v].position, Is.Not.EqualTo(oldContent[v].position), "and not the old one");
            }

            Assert.That(root.Storage.TryGetCommittedVertices(staysGeometry.vertexStart, staysGeometry.vertexCount, out NativeArray<VpRenderVertex> staysAfter), Is.True);
            for (int v = 0; v < staysBefore.Length; v++)
            {
                Assert.That(staysAfter[v].position, Is.EqualTo(staysBefore[v].position), "the body that stayed keeps its vertices");
                Assert.That(gpuVertices[staysGeometry.vertexStart + v].position, Is.EqualTo(staysBefore[v].position), "on the GPU too");
            }

            Assert.That(IsDrawn(root, stays), Is.True, "and is still drawn");

            var nextSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, next, new float4(1f, 0f, 0f, 0f), nextSides);
            yield return null;
            Assert.That(IsDrawn(root, nextSides[0]) && IsDrawn(root, nextSides[1]), Is.True, "and it is cut as usual");
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.WriteLine(root.Storage.DescribeRoom());
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A work still reading the lineage keeps its room.** The cut's geometry work is held after its physics is
        /// published and both sides are retired: the room stays while the work holds its input, and goes back once the
        /// work is let go and has ended.
        /// </summary>
        [UnityTest]
        public IEnumerator AWorkStillReadingALineage_KeepsItsRoomUntilItEnds()
        {
            HoldingExecutor geometry = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.GeometryPool
                    ? geometry = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            root.Lifetime.MarkLineage(body);
            yield return null;

            geometry.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            CutOperationId operation = AdmittedFor(root, new[] { body })[0];
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Published
                      && root.Owners.TryGet(op.positive, out _) && root.Owners.TryGet(op.negative, out _),
                "the cut's physics is published with both owners");
            LogicalCutOperation record = OperationOf(root, operation);
            Assert.That(root.Lifetime.TryRetire(record.positive, out string refusal), Is.True, refusal);
            Assert.That(root.Lifetime.TryRetire(record.negative, out refusal), Is.True, refusal);
            for (int i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(root.Geometry.VertexGroupsReleased, Is.Zero, "the held work still reads the lineage: the room is kept");

            geometry.HoldEverything = false;
            geometry.ReleaseEverything();
            yield return Until(() => root.Geometry.VertexGroupsReleased == 1, "the room went back once the work ended");
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
        }
    }
}
